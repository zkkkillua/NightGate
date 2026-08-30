using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class LegacyShutdownTaskAdapterTests
{
    private const string DefaultDefinitionFingerprint =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Scan_RecognizesCanonicalShutdownAndReturnsOnlyPrivacySafeFacts()
    {
        FakeLegacyScheduledTaskPlatform platform = new(
        [
            Task(
                @"\NightGate tests\old shutdown",
                enabled: true,
                Execute(@"%SystemRoot%\System32\shutdown.exe", "/s /f /t 0")),
        ]);
        LegacyShutdownTaskAdapter adapter = new(platform, ExpandEnvironment);

        LegacyShutdownTaskCandidate candidate = Assert.Single(adapter.Scan());

        Assert.Equal(@"\NightGate tests\old shutdown", candidate.TaskPath);
        Assert.True(candidate.WasEnabled);
        Assert.Matches("^[0-9a-f]{64}$", candidate.ActionFingerprint);
        Assert.Equal(
            ["ActionFingerprint", "TaskPath", "WasEnabled"],
            typeof(LegacyShutdownTaskCandidate)
                .GetProperties()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    public void Scan_EquivalentEnvironmentCaseQuotesAndArgumentOrderHaveStableFingerprint()
    {
        FakeLegacyScheduledTaskPlatform platform = new(
        [
            Task(
                @"\first",
                enabled: true,
                Execute(@"%SystemRoot%\System32\shutdown.exe", "/s /f /t 0")),
            Task(
                @"\second",
                enabled: false,
                Execute(@"""c:\WINDOWS\system32\SHUTDOWN.EXE""", "-T 0 -F -S")),
        ]);
        LegacyShutdownTaskAdapter adapter = new(platform, ExpandEnvironment);

        LegacyShutdownTaskCandidate[] candidates = adapter.Scan().ToArray();

        Assert.Equal(2, candidates.Length);
        Assert.Equal(candidates[0].ActionFingerprint, candidates[1].ActionFingerprint);
    }

    [Fact]
    public void Scan_NewCandidatePersistsCombinedDefinitionAndActionFingerprint()
    {
        LegacyShutdownTaskAdapter adapter = AdapterWith(
            Task(@"\candidate", true, Execute(ShutdownPath, "/s /f /t 0")));

        LegacyShutdownTaskCandidate candidate = Assert.Single(adapter.Scan());

        Assert.Equal(
            "0c5b442c313dc1ffedf62006d7c70cc6a9ab29677c26ea22771462d947c3a072",
            candidate.ActionFingerprint);
    }

    [Theory]
    [InlineData("/s")]
    [InlineData("-S")]
    [InlineData("/s /f /t 0")]
    [InlineData("/t 300 /S")]
    [InlineData("/s /hybrid /t 0")]
    [InlineData("/s /soft /d p:0:0 /c \"bed time\"")]
    [InlineData("/p")]
    public void Scan_AcceptsStrictShutdownOrPowerOffArguments(string arguments)
    {
        LegacyShutdownTaskAdapter adapter = AdapterWith(
            Task(@"\candidate", true, Execute(ShutdownPath, arguments)));

        Assert.Single(adapter.Scan());
    }

    [Theory]
    [InlineData("")]
    [InlineData("/r")]
    [InlineData("/r /t 0")]
    [InlineData("/g")]
    [InlineData("/l")]
    [InlineData("/h")]
    [InlineData("/a")]
    [InlineData("/s /r")]
    [InlineData("/s /t")]
    [InlineData("/s /t -1")]
    [InlineData("/s /t 1.5")]
    [InlineData("/s /unknown")]
    [InlineData("/p /t 0")]
    [InlineData("/p /hybrid")]
    [InlineData("/s /m \\\\remote")]
    [InlineData("/s /d invalid")]
    [InlineData("/s /c")]
    [InlineData("\"/s")]
    public void Scan_RejectsRestartLogoffHibernateAndAmbiguousArguments(string arguments)
    {
        LegacyShutdownTaskAdapter adapter = AdapterWith(
            Task(@"\not-a-candidate", true, Execute(ShutdownPath, arguments)));

        Assert.Empty(adapter.Scan());
    }

    [Fact]
    public void Scan_RejectsScriptsLookalikesAndMissingPaths()
    {
        LegacyShutdownTaskAdapter adapter = new(
            new FakeLegacyScheduledTaskPlatform(
            [
                Task(@"\script", true, Execute(@"C:\Scripts\shutdown.cmd", "/s")),
                Task(@"\lookalike", true, Execute(@"C:\Tools\shutdown-helper.exe", "/s")),
                Task(@"\missing", true, Execute(null, "/s")),
                Task(@"\unexpanded", true, Execute(@"%MissingRoot%\shutdown.exe", "/s")),
                Task(
                    @"\non-exec",
                    true,
                    new LegacyScheduledTaskActionSnapshot(
                        LegacyScheduledTaskActionKind.Other,
                        null,
                        null)),
            ]),
            ExpandEnvironment);

        Assert.Empty(adapter.Scan());
    }

    [Fact]
    public void Scan_AcceptsRelativeShutdownOnlyWhenWorkingDirectoryProvesSystem32()
    {
        LegacyShutdownTaskAdapter adapter = new(
            new FakeLegacyScheduledTaskPlatform(
            [
                Task(
                    @"\relative",
                    true,
                    Execute(
                        "shutdown.exe",
                        "/s /t 60",
                        @"%SystemRoot%\System32")),
                Task(
                    @"\multiple",
                    true,
                    Execute(@"C:\Tools\notify.exe", "bedtime"),
                    Execute(ShutdownPath, "/s /t 60")),
            ]),
            ExpandEnvironment);

        LegacyShutdownTaskCandidate[] candidates = adapter.Scan().ToArray();

        Assert.Equal([@"\relative", @"\multiple"], candidates.Select(item => item.TaskPath));
        Assert.All(candidates, item => Assert.True(item.WasEnabled));
        Assert.NotEqual(candidates[0].ActionFingerprint, candidates[1].ActionFingerprint);
    }

    [Fact]
    public void Scan_AcceptsBareShutdownWithoutWorkingDirectoryButRejectsAmbiguousResolution()
    {
        LegacyShutdownTaskAdapter adapter = new(
            new FakeLegacyScheduledTaskPlatform(
            [
                Task(
                    @"\absolute-lookalike",
                    true,
                    Execute(@"C:\Tools\shutdown.exe", "/s")),
                Task(@"\relative-no-directory", true, Execute("shutdown.exe", "/s")),
                Task(
                    @"\relative-wrong-directory",
                    true,
                    Execute("shutdown", "/s", @"C:\Tools")),
                Task(
                    @"\relative-partial-path",
                    true,
                    Execute(@".\shutdown.exe", "/s", @"C:\Windows\System32")),
                Task(
                    @"\canonical",
                    true,
                    Execute(@"C:\Windows\System32\shutdown.exe", "/s")),
            ]),
            ExpandEnvironment);

        Assert.Equal(
            [@"\relative-no-directory", @"\canonical"],
            adapter.Scan().Select(candidate => candidate.TaskPath));
    }

    [Fact]
    public void Scan_AcceptsDirectShutdownFromSupportedWindowsSystemDirectories()
    {
        LegacyShutdownTaskAdapter adapter = new(
            new FakeLegacyScheduledTaskPlatform(
            [
                Task(@"\system32", true, Execute(@"C:\Windows\System32\shutdown.exe", "/s /t \"0\"")),
                Task(@"\syswow64", true, Execute(@"C:\Windows\SysWOW64\shutdown.exe", "/s /t 60")),
                Task(@"\sysnative", true, Execute(@"C:\Windows\Sysnative\shutdown.exe", "/p")),
                Task(@"\bare", true, Execute("shutdown", "/s")),
            ]),
            ExpandEnvironment);

        Assert.Equal(
            [@"\system32", @"\syswow64", @"\sysnative", @"\bare"],
            adapter.Scan().Select(candidate => candidate.TaskPath));
    }

    [Fact]
    public void Scan_AcceptsUnambiguousCmdAndPowerShellShutdownWrappers()
    {
        LegacyShutdownTaskAdapter adapter = new(
            new FakeLegacyScheduledTaskPlatform(
            [
                Task(@"\cmd-c", true, Execute(@"C:\Windows\System32\cmd.exe", "/d /c shutdown.exe /s /t 60")),
                Task(@"\cmd-s-c", true, Execute("cmd.exe", "/s /c \"C:\\Windows\\SysWOW64\\shutdown.exe /s /t 0\"")),
                Task(@"\windows-powershell", true, Execute(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", "-NoProfile -Command \"shutdown.exe /s /t 0\"")),
                Task(@"\windows-powershell-positional", true, Execute(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", "shutdown.exe /s /t 0")),
                Task(@"\pwsh", true, Execute("pwsh.exe", "-NoLogo -Command shutdown.exe /p")),
                Task(@"\pwsh-program-files", true, Execute(@"%ProgramFiles%\PowerShell\7\pwsh.exe", "-Command \"Stop-Computer\"")),
                Task(@"\stop-computer", true, Execute("powershell.exe", "-NonInteractive -Command \"Stop-Computer -ComputerName localhost -Force\"")),
            ]),
            ExpandEnvironment);

        Assert.Equal(
            [@"\cmd-c", @"\cmd-s-c", @"\windows-powershell", @"\windows-powershell-positional", @"\pwsh", @"\pwsh-program-files", @"\stop-computer"],
            adapter.Scan().Select(candidate => candidate.TaskPath));
    }

    [Fact]
    public void Scan_FindsDirectAndWrapperTasksSoDisablingOneCannotHideTheOther()
    {
        LegacyShutdownTaskAdapter adapter = new(
            new FakeLegacyScheduledTaskPlatform(
            [
                Task(@"\already-disabled-direct", false, Execute(ShutdownPath, "/s /t 60")),
                Task(@"\still-enabled-wrapper", true, Execute(@"C:\Windows\System32\cmd.exe", "/c shutdown.exe /s /t 60")),
            ]),
            ExpandEnvironment);

        LegacyShutdownTaskCandidate[] candidates = adapter.Scan().ToArray();

        Assert.Equal(2, candidates.Length);
        Assert.False(candidates[0].WasEnabled);
        Assert.True(candidates[1].WasEnabled);
    }

    [Fact]
    public void Scan_WrapperFingerprintIncludesShellAndShutdownSemantics()
    {
        LegacyShutdownTaskAdapter adapter = new(
            new FakeLegacyScheduledTaskPlatform(
            [
                Task(@"\cmd-now", true, Execute(@"C:\Windows\System32\cmd.exe", "/c shutdown.exe /s /t 0")),
                Task(@"\cmd-later", true, Execute(@"C:\Windows\System32\cmd.exe", "/s /c \"shutdown.exe /s /t 60\"")),
                Task(@"\powershell", true, Execute(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", "-Command Stop-Computer")),
                Task(@"\powershell-force", true, Execute(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", "-NoProfile -Command \"Stop-Computer -Force\"")),
            ]),
            ExpandEnvironment);

        string[] fingerprints = adapter.Scan()
            .Select(candidate => candidate.ActionFingerprint)
            .ToArray();

        Assert.Equal(4, fingerprints.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\cmd.exe", "/c shutdown.exe /s & echo chained")]
    [InlineData(@"C:\Windows\System32\cmd.exe", "/s /c \"shutdown.exe /s | more\"")]
    [InlineData(@"C:\Windows\System32\cmd.exe", "/c call C:\\Scripts\\shutdown.cmd")]
    [InlineData(@"C:\Windows\System32\cmd.exe", "/c shutdown.exe /r /t 0")]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", "-Command \"shutdown.exe /s; Write-Host chained\"")]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", "-File C:\\Scripts\\shutdown.ps1")]
    [InlineData("pwsh.exe", "-EncodedCommand UwB0AG8AcAAtAEMAbwBtAHAAdQB0AGUAcgA=")]
    [InlineData("powershell.exe", "-Command \"Stop-Computer -ComputerName server01\"")]
    [InlineData("powershell.exe", "-Command \"Stop-Computer; Stop-Computer\"")]
    [InlineData("powershell.exe", "-Command \"Restart-Computer\"")]
    public void Scan_RejectsChainedScriptedRestartAndRemoteWrapperCommands(
        string executable,
        string arguments)
    {
        LegacyShutdownTaskAdapter adapter = AdapterWith(
            Task(@"\not-safe", true, Execute(executable, arguments)));

        Assert.Empty(adapter.Scan());
    }

    [Fact]
    public void Scan_FingerprintChangesWhenShutdownSemanticsChange()
    {
        LegacyShutdownTaskAdapter adapter = new(
            new FakeLegacyScheduledTaskPlatform(
            [
                Task(@"\now", true, Execute(ShutdownPath, "/s /t 0")),
                Task(@"\later", true, Execute(ShutdownPath, "/s /t 60")),
                Task(@"\power-off", true, Execute(ShutdownPath, "/p")),
            ]),
            ExpandEnvironment);

        string[] fingerprints = adapter.Scan()
            .Select(candidate => candidate.ActionFingerprint)
            .ToArray();

        Assert.Equal(3, fingerprints.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Scan_CandidateFingerprintIncludesTrustedDefinitionFingerprint()
    {
        LegacyScheduledTaskActionSnapshot action = Execute(ShutdownPath, "/s");
        LegacyShutdownTaskAdapter adapter = new(
            new FakeLegacyScheduledTaskPlatform(
            [
                TaskWithDefinition(
                    @"\definition-a",
                    true,
                    DefaultDefinitionFingerprint,
                    action),
                TaskWithDefinition(
                    @"\definition-b",
                    true,
                    "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                    action),
            ]),
            ExpandEnvironment);

        LegacyShutdownTaskCandidate[] candidates = adapter.Scan().ToArray();

        Assert.Equal(2, candidates.Length);
        Assert.NotEqual(
            candidates[0].ActionFingerprint,
            candidates[1].ActionFingerprint);
    }

    [Fact]
    public void Scan_FingerprintIncludesWorkingDirectoryAndEveryNativeActionType()
    {
        LegacyShutdownTaskAdapter adapter = new(
            new FakeLegacyScheduledTaskPlatform(
            [
                Task(
                    @"\working-a",
                    true,
                    Execute(ShutdownPath, "/s", @"C:\A"),
                    new(LegacyScheduledTaskActionKind.Other, null, null, NativeType: 5)),
                Task(
                    @"\working-b",
                    true,
                    Execute(ShutdownPath, "/s", @"C:\B"),
                    new(LegacyScheduledTaskActionKind.Other, null, null, NativeType: 6)),
            ]),
            ExpandEnvironment);

        string[] fingerprints = adapter.Scan()
            .Select(candidate => candidate.ActionFingerprint)
            .ToArray();

        Assert.Equal(2, fingerprints.Length);
        Assert.NotEqual(fingerprints[0], fingerprints[1]);
    }

    [Fact]
    public void Scan_PlatformOrEnvironmentFailureIsSafeEmptyOrSkipsOnlyThatTask()
    {
        FakeLegacyScheduledTaskPlatform unavailable = new([])
        {
            EnumerateException = new IOException("scheduler unavailable"),
        };
        FakeLegacyScheduledTaskPlatform partial = new(
        [
            Task(@"\broken", true, Execute(@"%Broken%\shutdown.exe", "/s")),
            Task(@"\valid", true, Execute(ShutdownPath, "/s")),
        ]);
        LegacyShutdownTaskAdapter throwingExpansion = new(
            partial,
            value => value.Contains("%Broken%", StringComparison.Ordinal)
                ? throw new InvalidOperationException("expansion failed")
                : ExpandEnvironment(value));

        LegacyShutdownTaskAdapter unavailableAdapter = new(unavailable, ExpandEnvironment);
        Assert.Empty(unavailableAdapter.Scan());
        LegacyShutdownTaskScanResult unavailableResult =
            unavailableAdapter.ScanWithStatus();
        Assert.False(unavailableResult.Available);
        Assert.Empty(unavailableResult.Candidates);
        Assert.Equal(@"\valid", Assert.Single(throwingExpansion.Scan()).TaskPath);
    }

    [Fact]
    public void Scan_FailingEnumerationDoesNotEscapePublicBoundary()
    {
        LegacyScheduledTaskSnapshot first = Task(
            @"\first",
            true,
            Execute(ShutdownPath, "/s"));
        LegacyShutdownTaskAdapter adapter = new(
            new FakeLegacyScheduledTaskPlatform(new ThrowAfterFirstList(first)),
            ExpandEnvironment);

        LegacyShutdownTaskCandidate candidate = Assert.Single(adapter.Scan());

        Assert.Equal(first.TaskPath, candidate.TaskPath);
    }

    private const string ShutdownPath = @"C:\Windows\System32\shutdown.exe";

    private static LegacyShutdownTaskAdapter AdapterWith(
        LegacyScheduledTaskSnapshot task) => new(
        new FakeLegacyScheduledTaskPlatform([task]),
        ExpandEnvironment);

    private static LegacyScheduledTaskSnapshot Task(
        string path,
        bool enabled,
        params LegacyScheduledTaskActionSnapshot[] actions) =>
        new(path, enabled, actions, DefaultDefinitionFingerprint);

    private static LegacyScheduledTaskSnapshot TaskWithDefinition(
        string path,
        bool enabled,
        string definitionFingerprint,
        params LegacyScheduledTaskActionSnapshot[] actions) =>
        new(path, enabled, actions, definitionFingerprint);

    private static LegacyScheduledTaskActionSnapshot Execute(
        string? executablePath,
        string? arguments,
        string? workingDirectory = null) =>
        new(
            LegacyScheduledTaskActionKind.Execute,
            executablePath,
            arguments,
            workingDirectory,
            NativeType: 0);

    private static string? ExpandEnvironment(string value)
    {
        if (value.Contains("%MissingRoot%", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return value.Replace(
                "%SystemRoot%",
                @"C:\Windows",
                StringComparison.OrdinalIgnoreCase)
            .Replace(
                "%ProgramFiles%",
                @"C:\Program Files",
                StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeLegacyScheduledTaskPlatform(
        IReadOnlyList<LegacyScheduledTaskSnapshot> tasks) :
        ILegacyScheduledTaskPlatform
    {
        public Exception? EnumerateException { get; init; }

        public LegacyScheduledTaskEnumerationResult Enumerate(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (EnumerateException is not null)
            {
                throw EnumerateException;
            }

            return new(true, tasks);
        }

        public LegacyScheduledTaskReadResult Read(
            string taskPath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public LegacyScheduledTaskSetEnabledStatus TrySetEnabled(
            LegacyScheduledTaskSnapshot expectedTask,
            bool enabled,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowAfterFirstList(LegacyScheduledTaskSnapshot first) :
        IReadOnlyList<LegacyScheduledTaskSnapshot>
    {
        public int Count => 2;

        public LegacyScheduledTaskSnapshot this[int index] => index switch
        {
            0 => first,
            _ => throw new IOException("enumeration failed"),
        };

        public IEnumerator<LegacyScheduledTaskSnapshot> GetEnumerator()
        {
            yield return first;
            throw new IOException("enumeration failed");
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }
}
