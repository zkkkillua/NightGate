using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace NightGate.Desktop;

internal sealed class DesktopSingleInstanceCoordinator : IDisposable
{
    private readonly Mutex _lifetime;
    private readonly EventWaitHandle _activation;
    private RegisteredWaitHandle? _activationRegistration;
    private int _listening;
    private int _disposed;

    private DesktopSingleInstanceCoordinator(
        Mutex lifetime,
        EventWaitHandle activation,
        bool isPrimary)
    {
        _lifetime = lifetime;
        _activation = activation;
        IsPrimary = isPrimary;
    }

    public bool IsPrimary { get; }

    public static DesktopSingleInstanceCoordinator CreateForCurrentUser()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string userSid = identity.User?.Value
            ?? throw new InvalidOperationException("The current Windows user does not have a SID.");
        return Create(userSid);
    }

    internal static DesktopSingleInstanceCoordinator Create(string identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        Mutex lifetime = new(
            initiallyOwned: false,
            name: $@"Local\NightGate.Desktop.Lifetime.{key}",
            createdNew: out bool createdNew);
        try
        {
            EventWaitHandle activation = new(
                initialState: false,
                mode: EventResetMode.AutoReset,
                name: $@"Local\NightGate.Desktop.Activate.{key}");
            return new(lifetime, activation, createdNew);
        }
        catch
        {
            lifetime.Dispose();
            throw;
        }
    }

    public void StartListening(Action activationRequested)
    {
        ArgumentNullException.ThrowIfNull(activationRequested);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!IsPrimary)
        {
            throw new InvalidOperationException("Only the primary desktop instance can listen.");
        }

        if (Interlocked.Exchange(ref _listening, 1) != 0)
        {
            throw new InvalidOperationException("The activation listener is already running.");
        }

        try
        {
            _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
                _activation,
                (_, timedOut) =>
                {
                    if (timedOut || Volatile.Read(ref _disposed) != 0)
                    {
                        return;
                    }

                    try
                    {
                        activationRequested();
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException
                        and not StackOverflowException
                        and not AccessViolationException)
                    {
                        // A failed window activation must not terminate the primary instance.
                    }
                },
                state: null,
                millisecondsTimeOutInterval: Timeout.Infinite,
                executeOnlyOnce: false);
        }
        catch
        {
            Interlocked.Exchange(ref _listening, 0);
            throw;
        }
    }

    public bool SignalExistingInstance()
    {
        if (IsPrimary || Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        try
        {
            return _activation.Set();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _activationRegistration?.Unregister(null);
        _activation.Dispose();
        _lifetime.Dispose();
    }
}
