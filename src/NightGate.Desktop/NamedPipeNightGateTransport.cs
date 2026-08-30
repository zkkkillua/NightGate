using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using NightGate.Protocol;

namespace NightGate.Desktop;

public sealed record NightGatePipeTransportOptions
{
    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromMilliseconds(int.MaxValue);

    public NightGatePipeTransportOptions(
        TimeSpan connectTimeout,
        TimeSpan writeTimeout,
        TimeSpan readTimeout)
    {
        ConnectTimeout = Validate(connectTimeout, nameof(connectTimeout));
        WriteTimeout = Validate(writeTimeout, nameof(writeTimeout));
        ReadTimeout = Validate(readTimeout, nameof(readTimeout));
    }

    public static NightGatePipeTransportOptions Default { get; } = new(
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(3));

    public TimeSpan ConnectTimeout { get; }

    public TimeSpan WriteTimeout { get; }

    public TimeSpan ReadTimeout { get; }

    private static TimeSpan Validate(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero || value > MaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }
}

public interface INightGatePipeConnectionFactory
{
    ValueTask<Stream> ConnectAsync(CancellationToken cancellationToken = default);
}

public sealed class NamedPipeClientConnectionFactory : INightGatePipeConnectionFactory
{
    public const string DefaultPipeName = "NightGateService";
    private readonly string _serverName;
    private readonly string _pipeName;

    public TokenImpersonationLevel ImpersonationLevel =>
        TokenImpersonationLevel.Impersonation;

    public NamedPipeClientConnectionFactory(
        string serverName = ".",
        string pipeName = DefaultPipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _serverName = serverName;
        _pipeName = pipeName;
    }

    public async ValueTask<Stream> ConnectAsync(
        CancellationToken cancellationToken = default)
    {
        NamedPipeClientStream stream = new(
            _serverName,
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            ImpersonationLevel);
        try
        {
            await stream.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

public sealed class NamedPipeNightGateTransport : INightGatePipeTransport
{
    private readonly INightGatePipeConnectionFactory _connectionFactory;
    private readonly NightGatePipeTransportOptions _options;

    public NamedPipeNightGateTransport()
        : this(new NamedPipeClientConnectionFactory(), NightGatePipeTransportOptions.Default)
    {
    }

    public NamedPipeNightGateTransport(
        INightGatePipeConnectionFactory connectionFactory,
        NightGatePipeTransportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
        _options = options ?? NightGatePipeTransportOptions.Default;
    }

    public async ValueTask<ReadOnlyMemory<byte>> ExchangeAsync(
        ReadOnlyMemory<byte> requestUtf8,
        CancellationToken cancellationToken = default)
    {
        Stream connection = await WithDeadlineAsync(
                token => _connectionFactory.ConnectAsync(token),
                _options.ConnectTimeout,
                "connect",
                cancellationToken)
            .ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await WithDeadlineAsync(
                    token => ProtocolFraming.WriteFrameAsync(connection, requestUtf8, token),
                    _options.WriteTimeout,
                    "write",
                    cancellationToken)
                .ConfigureAwait(false);
            return await WithDeadlineAsync(
                    token => ProtocolFraming.ReadFrameAsync(connection, token),
                    _options.ReadTimeout,
                    "read",
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async ValueTask<T> WithDeadlineAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        TimeSpan timeout,
        string operationName,
        CancellationToken callerCancellation)
    {
        using CancellationTokenSource deadline =
            CancellationTokenSource.CreateLinkedTokenSource(callerCancellation);
        deadline.CancelAfter(timeout);
        try
        {
            return await operation(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (!callerCancellation.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException($"Named-pipe {operationName} deadline elapsed.", exception);
        }
    }

    private static async ValueTask WithDeadlineAsync(
        Func<CancellationToken, ValueTask> operation,
        TimeSpan timeout,
        string operationName,
        CancellationToken callerCancellation)
    {
        using CancellationTokenSource deadline =
            CancellationTokenSource.CreateLinkedTokenSource(callerCancellation);
        deadline.CancelAfter(timeout);
        try
        {
            await operation(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (!callerCancellation.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException($"Named-pipe {operationName} deadline elapsed.", exception);
        }
    }
}
