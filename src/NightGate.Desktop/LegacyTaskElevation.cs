using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace NightGate.Desktop;

public interface ILegacyTaskElevationService
{
    ValueTask<LegacyTaskMutationStatus> DisableAsync(
        LegacyShutdownTaskCandidate candidate,
        CancellationToken cancellationToken = default);

    ValueTask<LegacyTaskMutationStatus> RestoreAsync(
        LegacyShutdownTaskCandidate candidate,
        CancellationToken cancellationToken = default);
}

internal sealed class WindowsLegacyTaskElevationService :
    ILegacyTaskElevationService
{
    public ValueTask<LegacyTaskMutationStatus> DisableAsync(
        LegacyShutdownTaskCandidate candidate,
        CancellationToken cancellationToken = default) =>
        RunElevatedAsync("disable", candidate, cancellationToken);

    public ValueTask<LegacyTaskMutationStatus> RestoreAsync(
        LegacyShutdownTaskCandidate candidate,
        CancellationToken cancellationToken = default) =>
        RunElevatedAsync("restore", candidate, cancellationToken);

    private static async ValueTask<LegacyTaskMutationStatus> RunElevatedAsync(
        string operation,
        LegacyShutdownTaskCandidate candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        string executable = Environment.ProcessPath ?? string.Empty;
        if (!OperatingSystem.IsWindows()
            || string.IsNullOrWhiteSpace(executable)
            || !File.Exists(executable))
        {
            return LegacyTaskMutationStatus.Unavailable;
        }

        ProcessStartInfo startInfo = new(executable)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add(LegacyTaskElevationEntryPoint.CommandFlag);
        startInfo.ArgumentList.Add(operation);
        startInfo.ArgumentList.Add(candidate.TaskPath);
        startInfo.ArgumentList.Add(candidate.ActionFingerprint);

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return LegacyTaskMutationStatus.Unavailable;
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return LegacyTaskElevationEntryPoint.FromExitCode(
                process.ExitCode,
                operation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is Win32Exception
            or InvalidOperationException
            or NotSupportedException)
        {
            // UAC cancellation and launch failures leave the scheduled task untouched.
            return LegacyTaskMutationStatus.Unavailable;
        }
    }
}

internal static class LegacyTaskElevationEntryPoint
{
    internal const string CommandFlag = "--legacy-task-elevated";
    private const int SuccessExitCode = 0;
    private const int UnchangedExitCode = 1;
    private const int ChangedExitCode = 2;
    private const int MissingExitCode = 3;
    private const int UnavailableExitCode = 4;
    private const int InvalidExitCode = 5;

    internal static bool TryRun(string[]? arguments, out int exitCode)
    {
        exitCode = InvalidExitCode;
        if (arguments is null
            || arguments.Length == 0
            || !string.Equals(arguments[0], CommandFlag, StringComparison.Ordinal))
        {
            return false;
        }

        if (arguments.Length != 4
            || arguments[1] is not ("disable" or "restore"))
        {
            return true;
        }

        LegacyShutdownTaskCandidate candidate = new(
            arguments[2],
            arguments[3],
            true);
        LegacyShutdownTaskAdapter adapter = new();
        LegacyTaskMutationResult? result = arguments[1] == "disable"
            ? adapter.ReconcilePrepared([candidate]).SingleOrDefault()
            : adapter.Restore([candidate]).SingleOrDefault();
        exitCode = ToExitCode(result?.Status);
        return true;
    }

    internal static LegacyTaskMutationStatus FromExitCode(
        int exitCode,
        string operation) => exitCode switch
        {
            SuccessExitCode => operation == "restore"
                ? LegacyTaskMutationStatus.Restored
                : LegacyTaskMutationStatus.Disabled,
            UnchangedExitCode => LegacyTaskMutationStatus.Unchanged,
            ChangedExitCode => LegacyTaskMutationStatus.Changed,
            MissingExitCode => LegacyTaskMutationStatus.Missing,
            InvalidExitCode => LegacyTaskMutationStatus.Invalid,
            _ => LegacyTaskMutationStatus.Unavailable,
        };

    private static int ToExitCode(LegacyTaskMutationStatus? status) => status switch
    {
        LegacyTaskMutationStatus.Disabled or LegacyTaskMutationStatus.Restored =>
            SuccessExitCode,
        LegacyTaskMutationStatus.Unchanged => UnchangedExitCode,
        LegacyTaskMutationStatus.Changed => ChangedExitCode,
        LegacyTaskMutationStatus.Missing => MissingExitCode,
        LegacyTaskMutationStatus.Invalid => InvalidExitCode,
        _ => UnavailableExitCode,
    };
}
