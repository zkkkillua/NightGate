using System.Security.Principal;

namespace NightGate.Desktop;

internal enum Win32ProcessIdentityReadStatus
{
    Success,
    Exited,
    NotFound,
    AccessDenied,
    Unavailable,
    Ambiguous,
}

internal sealed class Win32ProcessIdentityReadResult : IDisposable
{
    private SafeWin32ProcessHandle? _handle;

    private Win32ProcessIdentityReadResult(
        Win32ProcessIdentityReadStatus status,
        SafeWin32ProcessHandle? handle,
        ObservedProcessIdentity? identity)
    {
        Status = status;
        _handle = handle;
        Identity = identity;
    }

    public Win32ProcessIdentityReadStatus Status { get; }

    public SafeWin32ProcessHandle? Handle => _handle;

    public ObservedProcessIdentity? Identity { get; }

    public void Dispose() => Interlocked.Exchange(ref _handle, null)?.Dispose();

    internal static Win32ProcessIdentityReadResult Success(
        SafeWin32ProcessHandle handle,
        ObservedProcessIdentity identity) =>
        new(Win32ProcessIdentityReadStatus.Success, handle, identity);

    internal static Win32ProcessIdentityReadResult Failure(
        Win32ProcessIdentityReadStatus status) => new(status, null, null);
}

internal sealed class Win32ProcessIdentityReader
{
    private const int InitialImagePathCapacity = 260;
    private const Win32ProcessAccess RequiredAccess =
        Win32ProcessAccess.QueryLimitedInformation | Win32ProcessAccess.Synchronize;
    private const Win32ProcessAccess AllowedAccess =
        RequiredAccess | Win32ProcessAccess.Terminate;

    private readonly IWin32ProcessIdentityNative _native;

    public Win32ProcessIdentityReader()
        : this(Win32ProcessIdentityNative.Instance)
    {
    }

    internal Win32ProcessIdentityReader(IWin32ProcessIdentityNative native)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
    }

    public Win32ProcessIdentityReadResult OpenAndRead(
        int pid,
        Win32ProcessAccess access)
    {
        if (pid <= 0 || !IsAllowedAccess(access))
        {
            return Win32ProcessIdentityReadResult.Failure(
                Win32ProcessIdentityReadStatus.Ambiguous);
        }

        SafeWin32ProcessHandle? process = null;
        bool transferred = false;
        try
        {
            process = _native.OpenProcess(pid, access, out int openError);
            if (process is null || process.IsInvalid || process.IsClosed)
            {
                process?.Dispose();
                process = null;
                return Win32ProcessIdentityReadResult.Failure(
                    openError == Win32Error.Success
                        ? Win32ProcessIdentityReadStatus.Ambiguous
                        : MapOpenFailure(openError));
            }

            if (!_native.TryGetProcessId(process, out int actualPid, out int pidError))
            {
                return FailureAfterOpen(process, MapQueryFailure(pidError));
            }

            if (actualPid != pid || actualPid <= 0)
            {
                return FailureAfterOpen(
                    process,
                    Win32ProcessIdentityReadStatus.Ambiguous);
            }

            if (!_native.TryGetCreationFileTime(
                    process,
                    out long creationFileTimeUtc,
                    out int creationError))
            {
                return FailureAfterOpen(process, MapQueryFailure(creationError));
            }

            if (!TryConvertCreationInstant(
                    creationFileTimeUtc,
                    out DateTimeOffset creationInstantUtc))
            {
                return FailureAfterOpen(
                    process,
                    Win32ProcessIdentityReadStatus.Ambiguous);
            }

            if (!TryReadCanonicalImagePath(
                    process,
                    out string canonicalPath,
                    out int pathError))
            {
                return FailureAfterOpen(process, MapQueryFailure(pathError));
            }

            string canonicalSid;
            uint nativeSessionId;
            using (SafeWin32TokenHandle? token = _native.OpenProcessToken(
                       process,
                       Win32TokenAccess.Query,
                       out int tokenError))
            {
                if (token is null || token.IsInvalid || token.IsClosed)
                {
                    return FailureAfterOpen(
                        process,
                        tokenError == Win32Error.Success
                            ? Win32ProcessIdentityReadStatus.Ambiguous
                            : MapQueryFailure(tokenError));
                }

                if (!_native.TryGetTokenUserSid(
                        token,
                        out string userSid,
                        out int sidError))
                {
                    return FailureAfterOpen(process, MapQueryFailure(sidError));
                }

                if (!TryCanonicalizeSid(userSid, out canonicalSid))
                {
                    return FailureAfterOpen(
                        process,
                        Win32ProcessIdentityReadStatus.Ambiguous);
                }

                if (!_native.TryGetTokenSessionId(
                        token,
                        out nativeSessionId,
                        out int sessionError))
                {
                    return FailureAfterOpen(process, MapQueryFailure(sessionError));
                }

                if (nativeSessionId > int.MaxValue)
                {
                    return FailureAfterOpen(
                        process,
                        Win32ProcessIdentityReadStatus.Ambiguous);
                }
            }

            Win32ProcessWaitResult wait = _native.WaitForProcess(process, out _);
            if (wait == Win32ProcessWaitResult.Exited)
            {
                return Win32ProcessIdentityReadResult.Failure(
                    Win32ProcessIdentityReadStatus.Exited);
            }

            if (wait != Win32ProcessWaitResult.Alive)
            {
                return Win32ProcessIdentityReadResult.Failure(
                    Win32ProcessIdentityReadStatus.Ambiguous);
            }

            ProcessInstanceKey key = new(actualPid, creationInstantUtc.UtcTicks);
            ObservedProcessIdentity identity = new(
                key,
                creationInstantUtc,
                canonicalPath,
                canonicalSid,
                (int)nativeSessionId);
            Win32ProcessIdentityReadResult success =
                Win32ProcessIdentityReadResult.Success(process, identity);
            transferred = true;
            return success;
        }
        catch (Exception exception) when (IsPlatformUnavailable(exception))
        {
            return Win32ProcessIdentityReadResult.Failure(
                Win32ProcessIdentityReadStatus.Unavailable);
        }
        catch (Exception exception) when (IsAmbiguousNativeFailure(exception))
        {
            return Win32ProcessIdentityReadResult.Failure(
                Win32ProcessIdentityReadStatus.Ambiguous);
        }
        finally
        {
            if (!transferred)
            {
                process?.Dispose();
            }
        }
    }

    private static bool IsAllowedAccess(Win32ProcessAccess access) =>
        (access & RequiredAccess) == RequiredAccess
        && (access & ~AllowedAccess) == Win32ProcessAccess.None;

    private Win32ProcessIdentityReadResult FailureAfterOpen(
        SafeWin32ProcessHandle process,
        Win32ProcessIdentityReadStatus aliveFailure)
    {
        Win32ProcessWaitResult wait;
        try
        {
            wait = _native.WaitForProcess(process, out _);
        }
        catch (Exception exception) when (IsPlatformUnavailable(exception)
            || IsAmbiguousNativeFailure(exception))
        {
            return Win32ProcessIdentityReadResult.Failure(
                IsPlatformUnavailable(exception)
                    ? Win32ProcessIdentityReadStatus.Unavailable
                    : Win32ProcessIdentityReadStatus.Ambiguous);
        }

        return Win32ProcessIdentityReadResult.Failure(wait switch
        {
            Win32ProcessWaitResult.Exited => Win32ProcessIdentityReadStatus.Exited,
            Win32ProcessWaitResult.Alive => aliveFailure,
            _ => Win32ProcessIdentityReadStatus.Ambiguous,
        });
    }

    private bool TryReadCanonicalImagePath(
        SafeWin32ProcessHandle process,
        out string canonicalPath,
        out int error)
    {
        canonicalPath = string.Empty;
        int capacity = InitialImagePathCapacity;
        while (true)
        {
            Win32StringCallResult call =
                _native.QueryFullProcessImageName(process, capacity);
            if (call.Succeeded)
            {
                if (call.Error != Win32Error.Success
                    || call.Value is null
                    || call.Value.Length >= capacity
                    || !Win32ExecutablePathCanonicalizer.TryCanonicalize(
                        call.Value,
                        out canonicalPath))
                {
                    error = Win32Error.InvalidData;
                    canonicalPath = string.Empty;
                    return false;
                }

                error = Win32Error.Success;
                return true;
            }

            if (call.Error != Win32Error.InsufficientBuffer
                || capacity
                    == Win32ExecutablePathCanonicalizer.MaximumQueryBufferCharacters)
            {
                error = call.Error == Win32Error.Success
                    ? Win32Error.InvalidData
                    : call.Error;
                return false;
            }

            capacity = Math.Min(
                checked(capacity * 2),
                Win32ExecutablePathCanonicalizer.MaximumQueryBufferCharacters);
        }
    }

    private static bool TryConvertCreationInstant(
        long fileTimeUtc,
        out DateTimeOffset creationInstantUtc)
    {
        creationInstantUtc = default;
        if (fileTimeUtc < 0)
        {
            return false;
        }

        try
        {
            DateTime utc = DateTime.FromFileTimeUtc(fileTimeUtc);
            if (utc.Kind != DateTimeKind.Utc)
            {
                utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            }

            creationInstantUtc = new DateTimeOffset(utc);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryCanonicalizeSid(string? value, out string canonicalSid)
    {
        canonicalSid = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            SecurityIdentifier sid = new(value);
            canonicalSid = sid.Value;
            return string.Equals(value, canonicalSid, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static Win32ProcessIdentityReadStatus MapOpenFailure(int error) => error switch
    {
        Win32Error.AccessDenied => Win32ProcessIdentityReadStatus.AccessDenied,
        Win32Error.FileNotFound or Win32Error.InvalidParameter or Win32Error.NotFound =>
            Win32ProcessIdentityReadStatus.NotFound,
        Win32Error.CallNotImplemented or Win32Error.NotSupported or Win32Error.ProcNotFound =>
            Win32ProcessIdentityReadStatus.Unavailable,
        _ => Win32ProcessIdentityReadStatus.Ambiguous,
    };

    private static Win32ProcessIdentityReadStatus MapQueryFailure(int error) => error switch
    {
        Win32Error.AccessDenied => Win32ProcessIdentityReadStatus.AccessDenied,
        Win32Error.CallNotImplemented or Win32Error.NotSupported or Win32Error.ProcNotFound =>
            Win32ProcessIdentityReadStatus.Unavailable,
        _ => Win32ProcessIdentityReadStatus.Ambiguous,
    };

    private static bool IsPlatformUnavailable(Exception exception) =>
        exception is DllNotFoundException
            or EntryPointNotFoundException
            or PlatformNotSupportedException
            or BadImageFormatException;

    private static bool IsAmbiguousNativeFailure(Exception exception) =>
        exception is ArgumentException
            or InvalidOperationException
            or ObjectDisposedException
            or OverflowException;
}
