using System.IO;

namespace NightGate.Desktop;

public interface IRunningGameDetector
{
    ValueTask<bool> HasRunningGameAsync(
        IReadOnlyList<DesktopAppRuleDto> appRules,
        CurrentInteractiveIdentity identity,
        CancellationToken cancellationToken = default);
}

public sealed class WindowsRunningGameDetector : IRunningGameDetector
{
    internal const int MaximumSnapshotRows = 131_072;

    private readonly IWin32ProcessCatalogNative _native;
    private readonly IProcessCatalogIdentityReader _identities;

    public WindowsRunningGameDetector()
        : this(
            Win32ProcessCatalogNative.Instance,
            new Win32ProcessCatalogIdentityReader(new Win32ProcessIdentityReader()))
    {
    }

    internal WindowsRunningGameDetector(
        IWin32ProcessCatalogNative native,
        IProcessCatalogIdentityReader identities)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _identities = identities ?? throw new ArgumentNullException(nameof(identities));
    }

    public ValueTask<bool> HasRunningGameAsync(
        IReadOnlyList<DesktopAppRuleDto> appRules,
        CurrentInteractiveIdentity identity,
        CancellationToken cancellationToken = default) => new(Task.Run(
            () => Detect(appRules, identity, cancellationToken),
            cancellationToken));

    private bool Detect(
        IReadOnlyList<DesktopAppRuleDto> appRules,
        CurrentInteractiveIdentity identity,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (appRules is null
                || identity is null
                || string.IsNullOrWhiteSpace(identity.UserSid)
                || identity.SessionId < 0)
            {
                return false;
            }

            HashSet<string> gamePaths = new(StringComparer.OrdinalIgnoreCase);
            foreach (DesktopAppRuleDto rule in appRules)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (rule is { IsConfigured: true, Category: DesktopAppRuleCategory.Game }
                    && Win32ExecutablePathCanonicalizer.TryCanonicalize(
                        rule.RootExecutablePath,
                        out string canonicalPath))
                {
                    gamePaths.Add(canonicalPath);
                }
            }

            if (gamePaths.Count == 0)
            {
                return false;
            }

            HashSet<string> executableNames = gamePaths
                .Select(static path => Path.GetFileName(path)!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            HashSet<int> candidatePids = [];
            using (SafeWin32ProcessSnapshotHandle? snapshot =
                   _native.CreateProcessSnapshot(out _))
            {
                if (snapshot is null || snapshot.IsInvalid || snapshot.IsClosed)
                {
                    return false;
                }

                Win32ProcessCatalogMoveResult move = _native.ReadFirst(snapshot);
                int rowCount = 0;
                while (move.Status == Win32ProcessCatalogMoveStatus.Entry)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++rowCount > MaximumSnapshotRows
                        || move.Value is not { } row
                        || row.ProcessId < 0
                        || string.IsNullOrWhiteSpace(row.ExecutableName)
                        || !string.Equals(
                            Path.GetFileName(row.ExecutableName),
                            row.ExecutableName,
                            StringComparison.Ordinal))
                    {
                        return false;
                    }

                    if (row.ProcessId > 0 && executableNames.Contains(row.ExecutableName))
                    {
                        candidatePids.Add(row.ProcessId);
                    }

                    move = _native.ReadNext(snapshot);
                }

                if (move.Status != Win32ProcessCatalogMoveStatus.Completed
                    || move.Error != Win32Error.NoMoreFiles)
                {
                    return false;
                }
            }

            // This reader only requests query/synchronize access and disposes each handle.
            // Root identity alone drives the reminder; helper and voice rules are not games.
            foreach (int pid in candidatePids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProcessCatalogIdentityReadResult read = _identities.Read(pid);
                cancellationToken.ThrowIfCancellationRequested();
                if (read.Status == Win32ProcessIdentityReadStatus.Success
                    && read.Identity is { } process
                    && process.Key.Pid == pid
                    && process.SessionId == identity.SessionId
                    && string.Equals(process.UserSid, identity.UserSid, StringComparison.OrdinalIgnoreCase)
                    && Win32ExecutablePathCanonicalizer.TryCanonicalize(
                        process.ExecutablePath,
                        out string canonicalPath)
                    && gamePaths.Contains(canonicalPath))
                {
                    return true;
                }
            }

            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }
}
