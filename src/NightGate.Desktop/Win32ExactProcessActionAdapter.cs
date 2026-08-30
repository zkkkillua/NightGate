namespace NightGate.Desktop;

internal sealed class Win32ExactProcessActionAdapter : IExactProcessActionAdapter
{
    internal const uint WindowMessageClose = 0x0010;
    internal const uint TerminationExitCode = 1;

    private const Win32ProcessAccess CloseAccess =
        Win32ProcessAccess.QueryLimitedInformation | Win32ProcessAccess.Synchronize;
    private const Win32ProcessAccess TerminateAccess =
        CloseAccess | Win32ProcessAccess.Terminate;

    private readonly Win32ProcessIdentityReader _identityReader;
    private readonly IWin32ExactProcessActionNative _native;

    public Win32ExactProcessActionAdapter()
        : this(
            new Win32ProcessIdentityReader(),
            Win32ExactProcessActionNative.Instance)
    {
    }

    internal Win32ExactProcessActionAdapter(
        Win32ProcessIdentityReader identityReader,
        IWin32ExactProcessActionNative native)
    {
        _identityReader = identityReader
            ?? throw new ArgumentNullException(nameof(identityReader));
        _native = native ?? throw new ArgumentNullException(nameof(native));
    }

    public ValueTask<ProcessCloseOutcome> RequestCloseAsync(
        ProcessExactTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        bool sideEffectOccurred = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessCloseOutcome outcome = RequestCloseCore(
                target,
                cancellationToken,
                ref sideEffectOccurred);
            return ValueTask.FromResult(outcome);
        }
        catch (OperationCanceledException) when (sideEffectOccurred)
        {
            return ValueTask.FromResult(ProcessCloseOutcome.Ambiguous);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ValueTask.FromResult(ProcessCloseOutcome.Ambiguous);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return ValueTask.FromResult(
                !sideEffectOccurred && IsPlatformUnavailable(exception)
                    ? ProcessCloseOutcome.Unavailable
                    : ProcessCloseOutcome.Ambiguous);
        }
    }

    public ValueTask<ProcessTerminationOutcome> RequestTerminationAsync(
        ProcessExactTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        bool terminationCallStarted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessTerminationOutcome outcome = RequestTerminationCore(
                target,
                cancellationToken,
                ref terminationCallStarted);
            return ValueTask.FromResult(outcome);
        }
        catch (OperationCanceledException) when (
            !terminationCallStarted && cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ValueTask.FromResult(ProcessTerminationOutcome.Ambiguous);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return ValueTask.FromResult(
                IsPlatformUnavailable(exception)
                    ? ProcessTerminationOutcome.Unavailable
                    : ProcessTerminationOutcome.Ambiguous);
        }
    }

    private ProcessCloseOutcome RequestCloseCore(
        ProcessExactTarget target,
        CancellationToken cancellationToken,
        ref bool sideEffectOccurred)
    {
        using Win32ProcessIdentityReadResult read = _identityReader.OpenAndRead(
            target.InstanceKey.Pid,
            CloseAccess);
        ProcessCloseOutcome? readFailure = MapCloseReadFailure(read.Status);
        if (readFailure is not null)
        {
            return readFailure.Value;
        }

        if (read.Handle is null || read.Identity is null)
        {
            return ProcessCloseOutcome.Ambiguous;
        }

        if (!IsExactIdentityMatch(target, read.Identity))
        {
            return ProcessCloseOutcome.IdentityMismatch;
        }

        cancellationToken.ThrowIfCancellationRequested();
        Win32TopLevelWindowEnumerationResult enumeration =
            _native.EnumerateTopLevelWindows();
        ProcessCloseOutcome? enumerationFailure =
            ValidateEnumeration(enumeration);
        if (enumerationFailure is not null)
        {
            return enumerationFailure.Value;
        }

        cancellationToken.ThrowIfCancellationRequested();
        ProcessCloseOutcome? afterEnumeration = ResolveCloseWait(
            read.Handle,
            sideEffectOccurred);
        if (afterEnumeration is not null)
        {
            return afterEnumeration.Value;
        }

        List<nint> eligible = new(enumeration.Windows.Length);
        foreach (nint window in enumeration.Windows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Win32TopLevelWindowProbeResult probe =
                _native.ProbeTopLevelWindow(window);
            ProcessCloseOutcome? probeFailure = MapProbeFailure(
                probe.Status,
                sideEffectOccurred);
            if (probeFailure is not null)
            {
                return probeFailure.Value;
            }

            if (probe.State.ProcessId <= 0)
            {
                return ProcessCloseOutcome.Ambiguous;
            }

            if (probe.State.ProcessId == target.InstanceKey.Pid
                && probe.State.IsVisible
                && probe.State.IsEnabled
                && probe.State.Owner == nint.Zero)
            {
                eligible.Add(window);
            }
        }

        if (eligible.Count == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessCloseOutcome? finalWait = ResolveCloseWait(
                read.Handle,
                sideEffectOccurred);
            return finalWait ?? ProcessCloseOutcome.NoEligibleWindow;
        }

        foreach (nint window in eligible)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                if (sideEffectOccurred)
                {
                    return ProcessCloseOutcome.Ambiguous;
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            ProcessCloseOutcome? beforePost = ResolveCloseWait(
                read.Handle,
                sideEffectOccurred);
            if (beforePost is not null)
            {
                return beforePost.Value;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                if (sideEffectOccurred)
                {
                    return ProcessCloseOutcome.Ambiguous;
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            Win32TopLevelWindowProbeResult recheck =
                _native.ProbeTopLevelWindow(window);
            ProcessCloseOutcome? recheckFailure = MapProbeFailure(
                recheck.Status,
                sideEffectOccurred);
            if (recheckFailure is not null)
            {
                return recheckFailure.Value;
            }

            if (recheck.State.ProcessId != target.InstanceKey.Pid
                || !recheck.State.IsVisible
                || !recheck.State.IsEnabled
                || recheck.State.Owner != nint.Zero)
            {
                return ProcessCloseOutcome.Ambiguous;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                if (sideEffectOccurred)
                {
                    return ProcessCloseOutcome.Ambiguous;
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            if (!_native.TryPostMessage(
                    window,
                    WindowMessageClose,
                    wParam: 0,
                    lParam: 0,
                    out _))
            {
                return ProcessCloseOutcome.Ambiguous;
            }

            sideEffectOccurred = true;
            if (cancellationToken.IsCancellationRequested)
            {
                return ProcessCloseOutcome.Ambiguous;
            }
        }

        return ProcessCloseOutcome.Requested;
    }

    private ProcessTerminationOutcome RequestTerminationCore(
        ProcessExactTarget target,
        CancellationToken cancellationToken,
        ref bool terminationCallStarted)
    {
        using Win32ProcessIdentityReadResult read = _identityReader.OpenAndRead(
            target.InstanceKey.Pid,
            TerminateAccess);
        ProcessTerminationOutcome? readFailure =
            MapTerminationReadFailure(read.Status);
        if (readFailure is not null)
        {
            return readFailure.Value;
        }

        if (read.Handle is null || read.Identity is null)
        {
            return ProcessTerminationOutcome.Ambiguous;
        }

        if (!IsExactIdentityMatch(target, read.Identity))
        {
            return ProcessTerminationOutcome.IdentityMismatch;
        }

        cancellationToken.ThrowIfCancellationRequested();
        Win32ProcessWaitResult lastWait = _native.WaitForProcess(
            read.Handle,
            out _);
        if (lastWait == Win32ProcessWaitResult.Exited)
        {
            return ProcessTerminationOutcome.TargetExited;
        }

        if (lastWait != Win32ProcessWaitResult.Alive)
        {
            return ProcessTerminationOutcome.Ambiguous;
        }

        cancellationToken.ThrowIfCancellationRequested();
        terminationCallStarted = true;
        bool terminated = _native.TryTerminate(
            read.Handle,
            TerminationExitCode,
            out int terminationError);
        if (terminated)
        {
            return ProcessTerminationOutcome.Terminated;
        }

        Win32ProcessWaitResult afterFailure = _native.WaitForProcess(
            read.Handle,
            out _);
        if (afterFailure == Win32ProcessWaitResult.Exited)
        {
            return ProcessTerminationOutcome.TargetExited;
        }

        if (afterFailure != Win32ProcessWaitResult.Alive)
        {
            return ProcessTerminationOutcome.Ambiguous;
        }

        return IsUnavailableError(terminationError)
            ? ProcessTerminationOutcome.Unavailable
            : ProcessTerminationOutcome.Ambiguous;
    }

    private ProcessCloseOutcome? ResolveCloseWait(
        SafeWin32ProcessHandle handle,
        bool sideEffectOccurred)
    {
        Win32ProcessWaitResult wait = _native.WaitForProcess(handle, out _);
        return wait switch
        {
            Win32ProcessWaitResult.Alive => null,
            Win32ProcessWaitResult.Exited => sideEffectOccurred
                ? ProcessCloseOutcome.Requested
                : ProcessCloseOutcome.TargetExited,
            _ => ProcessCloseOutcome.Ambiguous,
        };
    }

    private static ProcessCloseOutcome? ValidateEnumeration(
        Win32TopLevelWindowEnumerationResult result)
    {
        if (result.Status == Win32TopLevelWindowEnumerationStatus.Unavailable)
        {
            return ProcessCloseOutcome.Unavailable;
        }

        if (result.Status != Win32TopLevelWindowEnumerationStatus.Complete)
        {
            return ProcessCloseOutcome.Ambiguous;
        }

        if (result.Error != Win32Error.Success
            || result.Windows.IsDefault
            || result.Windows.Length
                > Win32ExactProcessActionNative.MaximumTopLevelWindowCount)
        {
            return ProcessCloseOutcome.Ambiguous;
        }

        HashSet<nint> unique = [];
        foreach (nint window in result.Windows)
        {
            if (window == nint.Zero || !unique.Add(window))
            {
                return ProcessCloseOutcome.Ambiguous;
            }
        }

        return null;
    }

    private static ProcessCloseOutcome? MapProbeFailure(
        Win32WindowProbeStatus status,
        bool sideEffectOccurred) => status switch
        {
            Win32WindowProbeStatus.Success => null,
            Win32WindowProbeStatus.Unavailable when !sideEffectOccurred =>
                ProcessCloseOutcome.Unavailable,
            _ => ProcessCloseOutcome.Ambiguous,
        };

    private static ProcessCloseOutcome? MapCloseReadFailure(
        Win32ProcessIdentityReadStatus status) => status switch
        {
            Win32ProcessIdentityReadStatus.Success => null,
            Win32ProcessIdentityReadStatus.Exited
                or Win32ProcessIdentityReadStatus.NotFound =>
                ProcessCloseOutcome.TargetExited,
            Win32ProcessIdentityReadStatus.AccessDenied
                or Win32ProcessIdentityReadStatus.Unavailable =>
                ProcessCloseOutcome.Unavailable,
            _ => ProcessCloseOutcome.Ambiguous,
        };

    private static ProcessTerminationOutcome? MapTerminationReadFailure(
        Win32ProcessIdentityReadStatus status) => status switch
        {
            Win32ProcessIdentityReadStatus.Success => null,
            Win32ProcessIdentityReadStatus.Exited
                or Win32ProcessIdentityReadStatus.NotFound =>
                ProcessTerminationOutcome.TargetExited,
            Win32ProcessIdentityReadStatus.AccessDenied
                or Win32ProcessIdentityReadStatus.Unavailable =>
                ProcessTerminationOutcome.Unavailable,
            _ => ProcessTerminationOutcome.Ambiguous,
        };

    private static bool IsExactIdentityMatch(
        ProcessExactTarget target,
        ObservedProcessIdentity actual)
    {
        if (!Win32ExecutablePathCanonicalizer.TryCanonicalize(
                target.ExecutablePath,
                out string targetPath)
            || !Win32ExecutablePathCanonicalizer.TryCanonicalize(
                actual.ExecutablePath,
                out string actualPath))
        {
            return false;
        }

        return actual.Key == target.InstanceKey
            && actual.Key.Pid == target.InstanceKey.Pid
            && actual.Key.CreationUtcTicks == actual.CreationInstantUtc.UtcTicks
            && target.InstanceKey.CreationUtcTicks
                == target.CreationInstantUtc.UtcTicks
            && actual.CreationInstantUtc.UtcTicks
                == target.CreationInstantUtc.UtcTicks
            && string.Equals(
                actualPath,
                targetPath,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                actual.UserSid,
                target.UserSid,
                StringComparison.OrdinalIgnoreCase)
            && actual.SessionId == target.SessionId;
    }

    private static bool IsUnavailableError(int error) => error is
        Win32Error.AccessDenied
        or Win32Error.CallNotImplemented
        or Win32Error.NotSupported
        or Win32Error.ProcNotFound;

    private static bool IsPlatformUnavailable(Exception exception) =>
        exception is DllNotFoundException
            or EntryPointNotFoundException
            or PlatformNotSupportedException
            or BadImageFormatException;

    private static bool IsFatal(Exception exception) => exception is
        OutOfMemoryException
        or StackOverflowException
        or AccessViolationException;
}
