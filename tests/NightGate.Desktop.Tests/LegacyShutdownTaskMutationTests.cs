using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class LegacyShutdownTaskMutationTests
{
    private const string DefaultDefinitionFingerprint =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Version033System32ShutdownFingerprint =
        "cffec7075f9438e1cd944c79cf80cb7945fd5e6867e9b1e25e2610c0599debc9";
    private const string Version038System32ShutdownFingerprint =
        "9a29c51bae0d0337ae12ce622e3cdd7dcf777f92bd6cefe3a32edf74dc859494";

    [Fact]
    public void DisableSelected_OnlyTouchesSelectedCandidateAfterExactReread()
    {
        FakePlatform platform = new(
            Task(@"\first", true, "/s /t 0"),
            Task(@"\second", true, "/s /t 0"));
        LegacyShutdownTaskAdapter adapter = Adapter(platform);
        LegacyShutdownTaskCandidate selected = adapter.Scan()
            .Single(candidate => candidate.TaskPath == @"\second");

        LegacyTaskMutationResult result = Assert.Single(
            adapter.DisableSelected([selected]));

        Assert.Equal(LegacyTaskMutationStatus.Disabled, result.Status);
        Assert.Equal(selected.TaskPath, result.TaskPath);
        Assert.Equal(selected.ActionFingerprint, result.ActionFingerprint);
        Assert.Equal([(@"\second", false)], platform.SetCalls);
        Assert.True(platform.Tasks[@"\first"].Enabled);
        Assert.False(platform.Tasks[@"\second"].Enabled);
    }

    [Fact]
    public void DisableSelected_RejectsCandidateNotFromLatestScanWithoutReadingTask()
    {
        FakePlatform platform = new(Task(@"\first", true, "/s"));
        LegacyShutdownTaskAdapter adapter = Adapter(platform);
        LegacyShutdownTaskCandidate stale = Assert.Single(adapter.Scan());
        platform.Enumeration = [Task(@"\second", true, "/s")];
        _ = adapter.Scan();

        LegacyTaskMutationResult result = Assert.Single(
            adapter.DisableSelected([stale]));

        Assert.Equal(LegacyTaskMutationStatus.Invalid, result.Status);
        Assert.Empty(platform.ReadCalls);
        Assert.Empty(platform.SetCalls);
    }

    [Fact]
    public void DisableSelected_RefusesChangedFingerprintOrEnabledState()
    {
        FakePlatform platform = new(
            Task(@"\changed-action", true, "/s /t 0"),
            Task(@"\already-disabled", true, "/s"),
            Task(@"\was-disabled", false, "/s"));
        LegacyShutdownTaskAdapter adapter = Adapter(platform);
        LegacyShutdownTaskCandidate[] selected = adapter.Scan().ToArray();
        platform.Tasks[@"\changed-action"] = Task(
            @"\changed-action",
            true,
            "/s /t 60");
        platform.Tasks[@"\already-disabled"] = Task(
            @"\already-disabled",
            false,
            "/s");
        platform.Tasks[@"\was-disabled"] = Task(
            @"\was-disabled",
            true,
            "/s");

        LegacyTaskMutationResult[] results = adapter.DisableSelected(selected).ToArray();

        Assert.Equal(
            [
                LegacyTaskMutationStatus.Changed,
                LegacyTaskMutationStatus.Unchanged,
                LegacyTaskMutationStatus.Changed,
            ],
            results.Select(result => result.Status).ToArray());
        Assert.Empty(platform.SetCalls);
    }

    [Fact]
    public void DisableSelected_RefusesChangedNonExecuteActionPayloads()
    {
        LegacyScheduledTaskActionSnapshot shutdown = new(
            LegacyScheduledTaskActionKind.Execute,
            @"C:\Windows\System32\shutdown.exe",
            "/s /t 0");
        FakePlatform platform = new(
            TaskWithActions(
                @"\com-handler",
                true,
                shutdown,
                OtherAction(
                    nativeType: 5,
                    actionId: "handler",
                    new("ClassId", "{11111111-1111-1111-1111-111111111111}"),
                    new("Data", "payload-a"))),
            TaskWithActions(
                @"\email",
                true,
                shutdown,
                OtherAction(
                    nativeType: 6,
                    actionId: "email",
                    new("Subject", "subject-a"),
                    new("Body", "body-a"))),
            TaskWithActions(
                @"\show-message",
                true,
                shutdown,
                OtherAction(
                    nativeType: 7,
                    actionId: "message",
                    new("Title", "title-a"),
                    new("MessageBody", "message-a"))));
        LegacyShutdownTaskAdapter adapter = Adapter(platform);
        LegacyShutdownTaskCandidate[] selected = adapter.Scan().ToArray();

        platform.Tasks[@"\com-handler"] = TaskWithActions(
            @"\com-handler",
            true,
            shutdown,
            OtherAction(
                5,
                "handler",
                new("ClassId", "{11111111-1111-1111-1111-111111111111}"),
                new("Data", "payload-b")));
        platform.Tasks[@"\email"] = TaskWithActions(
            @"\email",
            true,
            shutdown,
            OtherAction(
                6,
                "email",
                new("Subject", "subject-a"),
                new("Body", "body-b")));
        platform.Tasks[@"\show-message"] = TaskWithActions(
            @"\show-message",
            true,
            shutdown,
            OtherAction(
                7,
                "message",
                new("Title", "title-b"),
                new("MessageBody", "message-a")));

        LegacyTaskMutationResult[] results = adapter.DisableSelected(selected).ToArray();

        Assert.All(
            results,
            result => Assert.Equal(LegacyTaskMutationStatus.Changed, result.Status));
        Assert.Empty(platform.SetCalls);
        Assert.All(platform.Tasks.Values, task => Assert.True(task.Enabled));
    }

    [Fact]
    public void DisableSelected_ReportsPerItemFailuresAndContinues()
    {
        FakePlatform platform = new(
            Task(@"\missing", true, "/s"),
            Task(@"\read-fault", true, "/s"),
            Task(@"\write-fault", true, "/s"),
            Task(@"\valid", true, "/s"));
        LegacyShutdownTaskAdapter adapter = Adapter(platform);
        LegacyShutdownTaskCandidate[] selected = adapter.Scan().ToArray();
        platform.ReadResults[@"\missing"] = LegacyScheduledTaskReadResult.Missing;
        platform.ReadExceptions[@"\read-fault"] = new IOException("access denied");
        platform.SetResults[@"\write-fault"] =
            LegacyScheduledTaskSetEnabledStatus.Unavailable;

        LegacyTaskMutationResult[] results = adapter.DisableSelected(selected).ToArray();

        Assert.Equal(
            [
                LegacyTaskMutationStatus.Missing,
                LegacyTaskMutationStatus.Unavailable,
                LegacyTaskMutationStatus.Unavailable,
                LegacyTaskMutationStatus.Disabled,
            ],
            results.Select(result => result.Status).ToArray());
        Assert.False(platform.Tasks[@"\valid"].Enabled);
    }

    [Fact]
    public void DisableSelected_RefusesReplacementBetweenRereadAndEnabledWrite()
    {
        FakePlatform platform = new(Task(@"\raced", true, "/s /t 0"));
        LegacyShutdownTaskAdapter adapter = Adapter(platform);
        LegacyShutdownTaskCandidate selected = Assert.Single(adapter.Scan());
        platform.BeforeSet = expected =>
            platform.Tasks[expected.TaskPath] = Task(
                expected.TaskPath,
                true,
                "/s /t 60");

        LegacyTaskMutationResult result = Assert.Single(
            adapter.DisableSelected([selected]));

        Assert.Equal(LegacyTaskMutationStatus.Changed, result.Status);
        Assert.True(platform.Tasks[@"\raced"].Enabled);
    }

    [Fact]
    public void Restore_ReenablesOnlyOriginallyEnabledExactDisabledTask()
    {
        FakePlatform platform = new(
            Task(@"\originally-enabled", true, "/s /t 0"),
            Task(@"\originally-disabled", false, "/s"));
        LegacyShutdownTaskAdapter adapter = Adapter(platform);
        LegacyShutdownTaskCandidate[] records = adapter.Scan().ToArray();
        LegacyShutdownTaskCandidate enabled = records.Single(record => record.WasEnabled);
        Assert.Equal(
            LegacyTaskMutationStatus.Disabled,
            Assert.Single(adapter.DisableSelected([enabled])).Status);
        platform.SetCalls.Clear();

        LegacyTaskMutationResult[] results = adapter.Restore(records).ToArray();

        Assert.Equal(
            [LegacyTaskMutationStatus.Restored, LegacyTaskMutationStatus.Unchanged],
            results.Select(result => result.Status).ToArray());
        Assert.Equal([(@"\originally-enabled", true)], platform.SetCalls);
        Assert.True(platform.Tasks[@"\originally-enabled"].Enabled);
        Assert.False(platform.Tasks[@"\originally-disabled"].Enabled);
    }

    [Fact]
    public void Restore_IsIdempotentAndRefusesReplacementMissingOrUnavailableTask()
    {
        FakePlatform platform = new(
            Task(@"\already-restored", true, "/s"),
            Task(@"\replacement", true, "/s"),
            Task(@"\missing", true, "/s"),
            Task(@"\unavailable", true, "/s"));
        LegacyShutdownTaskAdapter adapter = Adapter(platform);
        LegacyShutdownTaskCandidate[] records = adapter.Scan().ToArray();
        platform.Tasks[@"\replacement"] = Task(@"\replacement", false, "/p");
        platform.ReadResults[@"\missing"] = LegacyScheduledTaskReadResult.Missing;
        platform.ReadResults[@"\unavailable"] =
            LegacyScheduledTaskReadResult.Unavailable;

        LegacyTaskMutationResult[] results = adapter.Restore(records).ToArray();

        Assert.Equal(
            [
                LegacyTaskMutationStatus.Unchanged,
                LegacyTaskMutationStatus.Changed,
                LegacyTaskMutationStatus.Missing,
                LegacyTaskMutationStatus.Unavailable,
            ],
            results.Select(result => result.Status).ToArray());
        Assert.Empty(platform.SetCalls);
    }

    [Fact]
    public void Restore_RevalidatesFingerprintEvenWhenTaskWasOriginallyDisabled()
    {
        FakePlatform platform = new(Task(@"\originally-disabled", false, "/s"));
        LegacyShutdownTaskAdapter adapter = Adapter(platform);
        LegacyShutdownTaskCandidate record = Assert.Single(adapter.Scan());
        platform.Tasks[record.TaskPath] = Task(record.TaskPath, false, "/p");

        LegacyTaskMutationResult result = Assert.Single(adapter.Restore([record]));

        Assert.Equal(LegacyTaskMutationStatus.Changed, result.Status);
        Assert.Equal([record.TaskPath], platform.ReadCalls);
        Assert.Empty(platform.SetCalls);
    }

    [Theory]
    [InlineData("trigger")]
    [InlineData("principal")]
    [InlineData("settings")]
    public void Restore_RefusesDefinitionDriftEvenWhenActionsAreUnchanged(
        string changedPart)
    {
        FakePlatform platform = new(Task(@"\definition-drift", true, "/s"));
        LegacyShutdownTaskAdapter adapter = Adapter(platform);
        LegacyShutdownTaskCandidate candidate = Assert.Single(adapter.Scan());
        Assert.Equal(
            LegacyTaskMutationStatus.Disabled,
            Assert.Single(adapter.DisableSelected([candidate])).Status);
        string changedDefinitionFingerprint = changedPart switch
        {
            "trigger" => new string('b', 64),
            "principal" => new string('c', 64),
            "settings" => new string('d', 64),
            _ => throw new ArgumentOutOfRangeException(nameof(changedPart)),
        };
        platform.Tasks[candidate.TaskPath] = Task(
            candidate.TaskPath,
            false,
            "/s",
            changedDefinitionFingerprint);

        LegacyTaskMutationResult restored = Assert.Single(
            adapter.Restore([candidate]));

        Assert.Equal(LegacyTaskMutationStatus.Changed, restored.Status);
        Assert.False(platform.Tasks[candidate.TaskPath].Enabled);
    }

    [Fact]
    public void ReconcilePrepared_RecoversCrashBeforeOrAfterExactDisable()
    {
        FakePlatform platform = new(
            Task(@"\still-enabled", true, "/s"),
            Task(@"\already-disabled", true, "/s"));
        LegacyShutdownTaskAdapter adapter = Adapter(platform);
        LegacyShutdownTaskCandidate[] records = adapter.Scan().ToArray();
        platform.Tasks[@"\already-disabled"] = Task(
            @"\already-disabled",
            false,
            "/s");

        LegacyTaskMutationResult[] results = adapter
            .ReconcilePrepared(records)
            .ToArray();

        Assert.Equal(
            [LegacyTaskMutationStatus.Disabled, LegacyTaskMutationStatus.Unchanged],
            results.Select(result => result.Status).ToArray());
        Assert.False(platform.Tasks[@"\still-enabled"].Enabled);
        Assert.False(platform.Tasks[@"\already-disabled"].Enabled);
    }

    [Fact]
    public void LegacyFingerprint_CanRestoreOnlyDefinitionCapturedByThisDisable()
    {
        FakePlatform platform = new(
            Task(@"\still-enabled", true, "/s"),
            Task(@"\already-disabled", false, "/s"));
        LegacyShutdownTaskAdapter adapter = Adapter(platform);
        LegacyShutdownTaskCandidate stillEnabled = new(
            @"\still-enabled",
            Version038System32ShutdownFingerprint,
            WasEnabled: true);
        LegacyShutdownTaskCandidate alreadyDisabled = new(
            @"\already-disabled",
            Version038System32ShutdownFingerprint,
            WasEnabled: true);

        LegacyTaskMutationResult reconciled = Assert.Single(
            adapter.ReconcilePrepared([stillEnabled]));
        LegacyTaskMutationResult reconciledAfterLostCompletion = Assert.Single(
            adapter.ReconcilePrepared([stillEnabled]));
        LegacyTaskObservationResult observedCaptured = Assert.Single(
            adapter.Observe([stillEnabled]));
        LegacyTaskMutationResult restoredCaptured = Assert.Single(
            adapter.Restore([stillEnabled]));
        LegacyTaskMutationResult refusedUnproven = Assert.Single(
            adapter.Restore([alreadyDisabled]));

        Assert.Equal(LegacyTaskMutationStatus.Disabled, reconciled.Status);
        Assert.Equal(
            LegacyTaskMutationStatus.Unchanged,
            reconciledAfterLostCompletion.Status);
        Assert.Equal(
            LegacyTaskObservationStatus.MatchingDisabled,
            observedCaptured.Status);
        Assert.Equal(LegacyTaskMutationStatus.Restored, restoredCaptured.Status);
        Assert.Equal(LegacyTaskMutationStatus.Changed, refusedUnproven.Status);
        Assert.True(platform.Tasks[stillEnabled.TaskPath].Enabled);
        Assert.False(platform.Tasks[alreadyDisabled.TaskPath].Enabled);
    }

    [Fact]
    public void Observe_RefusesUnprovenLegacyDisabledTaskWithoutMutation()
    {
        FakePlatform platform = new(Task(@"\already-disabled", false, "/s"));
        LegacyShutdownTaskAdapter adapter = Adapter(platform);
        LegacyShutdownTaskCandidate legacyRecord = new(
            @"\already-disabled",
            Version033System32ShutdownFingerprint,
            WasEnabled: true);

        LegacyTaskObservationResult observation = Assert.Single(
            adapter.Observe([legacyRecord]));

        Assert.Equal(
            LegacyTaskObservationStatus.Changed,
            observation.Status);
        Assert.Equal([legacyRecord.TaskPath], platform.ReadCalls);
        Assert.Empty(platform.SetCalls);
        Assert.False(platform.Tasks[legacyRecord.TaskPath].Enabled);
    }

    [Fact]
    public void Observe_NeverMutatesEnabledChangedMissingOrUnavailableTasks()
    {
        FakePlatform platform = new(
            Task(@"\enabled", true, "/s"),
            Task(@"\changed", false, "/s /t 60"),
            Task(@"\missing", false, "/s"),
            Task(@"\unavailable", false, "/s"));
        platform.ReadResults[@"\missing"] = LegacyScheduledTaskReadResult.Missing;
        platform.ReadExceptions[@"\unavailable"] = new IOException("access denied");
        LegacyShutdownTaskAdapter adapter = Adapter(platform);
        LegacyShutdownTaskCandidate[] records =
        [
            new(@"\enabled", Version033System32ShutdownFingerprint, true),
            new(@"\changed", Version033System32ShutdownFingerprint, true),
            new(@"\missing", Version033System32ShutdownFingerprint, true),
            new(@"\unavailable", Version033System32ShutdownFingerprint, true),
        ];

        LegacyTaskObservationResult[] observations = adapter.Observe(records).ToArray();

        Assert.Equal(
            [
                LegacyTaskObservationStatus.Changed,
                LegacyTaskObservationStatus.Changed,
                LegacyTaskObservationStatus.Missing,
                LegacyTaskObservationStatus.Unavailable,
            ],
            observations.Select(item => item.Status).ToArray());
        Assert.Empty(platform.SetCalls);
        Assert.True(platform.Tasks[@"\enabled"].Enabled);
        Assert.False(platform.Tasks[@"\changed"].Enabled);
    }

    [Fact]
    public void Version033SimpleFingerprint_NeverAuthorizesIncompleteOrChangedActionShape()
    {
        LegacyScheduledTaskActionSnapshot shutdown = new(
            LegacyScheduledTaskActionKind.Execute,
            @"C:\Windows\System32\shutdown.exe",
            "/s");
        FakePlatform platform = new(
            TaskWithActions(
                @"\multiple",
                true,
                shutdown,
                new(
                    LegacyScheduledTaskActionKind.Execute,
                    @"C:\Tools\notify.exe",
                    "bedtime")),
            TaskWithActions(
                @"\working-directory",
                true,
                shutdown with { WorkingDirectory = @"C:\Windows\Temp" }),
            TaskWithActions(
                @"\action-id",
                true,
                shutdown with { ActionId = "shutdown-action" }),
            TaskWithActions(
                @"\native-type",
                true,
                shutdown with { NativeType = 5 }),
            TaskWithActions(
                @"\execute-properties",
                true,
                shutdown with
                {
                    Properties = [new("Hidden", "payload")],
                }),
            TaskWithActions(
                @"\lookalike",
                true,
                shutdown with { ExecutablePath = @"C:\Tools\shutdown.exe" }),
            TaskWithActions(
                @"\relative",
                true,
                shutdown with
                {
                    ExecutablePath = "shutdown.exe",
                    WorkingDirectory = @"C:\Windows\System32",
                }),
            Task(@"\changed-arguments", true, "/s /t 60"));
        LegacyShutdownTaskAdapter adapter = Adapter(platform);
        LegacyShutdownTaskCandidate[] records = platform.Tasks.Keys
            .Select(path => new LegacyShutdownTaskCandidate(
                path,
                Version033System32ShutdownFingerprint,
                WasEnabled: true))
            .ToArray();

        LegacyTaskMutationResult[] results = adapter
            .ReconcilePrepared(records)
            .ToArray();

        Assert.All(
            results,
            result => Assert.Equal(LegacyTaskMutationStatus.Changed, result.Status));
        Assert.Empty(platform.SetCalls);
        Assert.All(platform.Tasks.Values, task => Assert.True(task.Enabled));
    }

    [Fact]
    public void Mutations_PropagateOnlyCallerCancellation()
    {
        FakePlatform platform = new(Task(@"\candidate", true, "/s"));
        LegacyShutdownTaskAdapter adapter = Adapter(platform);
        LegacyShutdownTaskCandidate selected = Assert.Single(adapter.Scan());
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            adapter.DisableSelected([selected], cancellation.Token));
        Assert.Throws<OperationCanceledException>(() =>
            adapter.Restore([selected], cancellation.Token));
        Assert.Throws<OperationCanceledException>(() =>
            adapter.ReconcilePrepared([selected], cancellation.Token));
        Assert.Throws<OperationCanceledException>(() =>
            adapter.Observe([selected], cancellation.Token));
        Assert.Empty(platform.SetCalls);
    }

    private static LegacyShutdownTaskAdapter Adapter(FakePlatform platform) =>
        new(
            platform,
            value => value.Replace(
                "%SystemRoot%",
                @"C:\Windows",
                StringComparison.OrdinalIgnoreCase));

    private static LegacyScheduledTaskSnapshot Task(
        string path,
        bool enabled,
        string arguments,
        string definitionFingerprint = DefaultDefinitionFingerprint) => new(
        path,
        enabled,
        [
            new LegacyScheduledTaskActionSnapshot(
                LegacyScheduledTaskActionKind.Execute,
                @"C:\Windows\System32\shutdown.exe",
                arguments),
        ],
        definitionFingerprint);

    private static LegacyScheduledTaskSnapshot TaskWithActions(
        string path,
        bool enabled,
        params LegacyScheduledTaskActionSnapshot[] actions) =>
        new(path, enabled, actions, DefaultDefinitionFingerprint);

    private static LegacyScheduledTaskActionSnapshot OtherAction(
        int nativeType,
        string actionId,
        params LegacyScheduledTaskActionPropertySnapshot[] properties) => new(
        LegacyScheduledTaskActionKind.Other,
        null,
        null,
        NativeType: nativeType,
        ActionId: actionId,
        Properties: properties);

    private sealed class FakePlatform : ILegacyScheduledTaskPlatform
    {
        public FakePlatform(params LegacyScheduledTaskSnapshot[] tasks)
        {
            Enumeration = tasks;
            Tasks = tasks.ToDictionary(
                task => task.TaskPath,
                StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyList<LegacyScheduledTaskSnapshot> Enumeration { get; set; }

        public Dictionary<string, LegacyScheduledTaskSnapshot> Tasks { get; }

        public Dictionary<string, LegacyScheduledTaskReadResult> ReadResults { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, Exception> ReadExceptions { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, LegacyScheduledTaskSetEnabledStatus> SetResults { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Action<LegacyScheduledTaskSnapshot>? BeforeSet { get; set; }

        public List<string> ReadCalls { get; } = [];

        public List<(string TaskPath, bool Enabled)> SetCalls { get; } = [];

        public LegacyScheduledTaskEnumerationResult Enumerate(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(true, Enumeration);
        }

        public LegacyScheduledTaskReadResult Read(
            string taskPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCalls.Add(taskPath);
            if (ReadExceptions.TryGetValue(taskPath, out Exception? exception))
            {
                throw exception;
            }

            if (ReadResults.TryGetValue(taskPath, out LegacyScheduledTaskReadResult? result))
            {
                return result;
            }

            return Tasks.TryGetValue(taskPath, out LegacyScheduledTaskSnapshot? task)
                ? LegacyScheduledTaskReadResult.Found(task)
                : LegacyScheduledTaskReadResult.Missing;
        }

        public LegacyScheduledTaskSetEnabledStatus TrySetEnabled(
            LegacyScheduledTaskSnapshot expectedTask,
            bool enabled,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string taskPath = expectedTask.TaskPath;
            SetCalls.Add((taskPath, enabled));
            BeforeSet?.Invoke(expectedTask);
            if (SetResults.TryGetValue(
                    taskPath,
                    out LegacyScheduledTaskSetEnabledStatus result))
            {
                return result;
            }

            if (!Tasks.TryGetValue(taskPath, out LegacyScheduledTaskSnapshot? task))
            {
                return LegacyScheduledTaskSetEnabledStatus.Missing;
            }

            if (!LegacyScheduledTaskSnapshotComparer.EqualsExact(task, expectedTask))
            {
                return LegacyScheduledTaskSetEnabledStatus.Changed;
            }

            if (task.Enabled == enabled)
            {
                return LegacyScheduledTaskSetEnabledStatus.Unchanged;
            }

            Tasks[taskPath] = task with { Enabled = enabled };
            return LegacyScheduledTaskSetEnabledStatus.Updated;
        }
    }
}
