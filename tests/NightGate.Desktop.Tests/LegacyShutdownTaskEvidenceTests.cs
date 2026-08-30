using System.Text.Json;
using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class LegacyShutdownTaskEvidenceTests
{
    private static readonly TimeZoneInfo ChinaTime = TimeZoneInfo.CreateCustomTimeZone(
        "NightGate evidence tests",
        TimeSpan.FromHours(8),
        "NightGate evidence tests",
        "NightGate evidence tests");

    [Fact]
    public void Adapter_RuntimeEvidenceUsesOnlyReadAndNeverTouchesEnabledMutation()
    {
        DateTimeOffset lastRunUtc = new(2026, 7, 18, 16, 10, 1, TimeSpan.Zero);
        EvidenceTaskPlatform platform = new(lastRunUtc, lastTaskResult: 0);
        LegacyShutdownTaskAdapter adapter = new(platform, ExpandWindows);
        LegacyShutdownTaskCandidate candidate = Assert.Single(adapter.Scan());

        LegacyShutdownTaskRuntimeEvidence evidence = Assert.Single(
            ((ILegacyShutdownTaskEvidenceReader)adapter).ReadRuntimeEvidence([candidate]));

        Assert.Equal(LegacyTaskObservationStatus.MatchingDisabled, evidence.Status);
        Assert.False(evidence.Enabled);
        Assert.Equal(lastRunUtc, evidence.LastRunTimeUtc);
        Assert.Equal(0, evidence.LastTaskResult);
        Assert.Equal(1, platform.ReadCount);
        Assert.Equal(0, platform.SetEnabledCount);
    }

    [Fact]
    public async Task CaptureService_WritesExplicitCompleteEvidenceForManagedTask()
    {
        DateTimeOffset checkedAt = new(2026, 7, 20, 0, 11, 4, TimeSpan.FromHours(8));
        DateTimeOffset lastRunUtc = new(2026, 7, 18, 16, 10, 1, TimeSpan.Zero);
        FakeLegacyMigrationService service = new(Migration());
        FakeEvidenceReader reader = new(new LegacyShutdownTaskRuntimeEvidence(
            TaskPath,
            Fingerprint,
            LegacyTaskObservationStatus.MatchingDisabled,
            Enabled: false,
            LastRunTimeUtc: lastRunUtc,
            LastTaskResult: 0));
        LegacyShutdownTaskEvidenceCaptureService capture = new(
            service,
            reader,
            ChinaTime);

        LegacyShutdownTaskEvidenceDocument document = await capture.CaptureAsync(checkedAt);

        Assert.Equal(1, document.SchemaVersion);
        Assert.Equal(new DateOnly(2026, 7, 20), document.ProbeDateLocal);
        Assert.Equal(checkedAt, document.CheckedAtLocal);
        Assert.Equal(checkedAt.ToUniversalTime(), document.CheckedAtUtc);
        Assert.Equal("complete", document.Status);
        Assert.Null(document.Error);
        LegacyShutdownTaskEvidenceEntry task = Assert.Single(document.Tasks);
        Assert.Equal(MigrationId, task.MigrationId);
        Assert.Equal(TaskPath, task.TaskPath);
        Assert.Equal("disabled", task.MigrationStatus);
        Assert.Equal("matchingDisabled", task.IdentityStatus);
        Assert.False(task.Enabled);
        Assert.Equal(lastRunUtc, task.LastRunTimeUtc);
        Assert.Equal(lastRunUtc.ToOffset(TimeSpan.FromHours(8)), task.LastRunTimeLocal);
        Assert.Equal(0, task.LastTaskResult);
    }

    [Fact]
    public async Task CaptureService_FailureStillProducesFreshInconclusiveDocument()
    {
        DateTimeOffset checkedAt = new(2026, 7, 20, 0, 11, 4, TimeSpan.FromHours(8));
        LegacyShutdownTaskEvidenceCaptureService capture = new(
            new FakeLegacyMigrationService(error: "service-unavailable"),
            new FakeEvidenceReader(),
            ChinaTime);

        LegacyShutdownTaskEvidenceDocument document = await capture.CaptureAsync(checkedAt);

        Assert.Equal(new DateOnly(2026, 7, 20), document.ProbeDateLocal);
        Assert.Equal(checkedAt, document.CheckedAtLocal);
        Assert.Equal("inconclusive", document.Status);
        Assert.Equal("service-unavailable", document.Error);
        Assert.Empty(document.Tasks);
    }

    [Fact]
    public async Task Runtime_CatchesUpAfter0011OnlyOnceAndCapturesAgainNextDay()
    {
        ManualEvidenceClock clock = new(
            new DateTimeOffset(2026, 7, 20, 0, 12, 0, TimeSpan.FromHours(8)));
        FakeEvidenceCapture capture = new();
        MemoryEvidenceSink sink = new();
        LegacyShutdownTaskEvidenceRuntime runtime = new(capture, sink, clock, ChinaTime);

        Assert.True(await runtime.CaptureIfDueAsync());
        Assert.False(await runtime.CaptureIfDueAsync());
        Assert.Single(capture.CheckedAt);
        Assert.Single(sink.Documents);

        clock.Now = new DateTimeOffset(2026, 7, 21, 0, 10, 59, TimeSpan.FromHours(8));
        Assert.False(await runtime.CaptureIfDueAsync());
        clock.Now = new DateTimeOffset(2026, 7, 21, 0, 11, 1, TimeSpan.FromHours(8));
        Assert.True(await runtime.CaptureIfDueAsync());
        Assert.Equal(2, capture.CheckedAt.Count);
        Assert.Equal(2, sink.Documents.Count);
    }

    [Fact]
    public async Task Runtime_ExistingSameDayEvidencePreventsRestartRewrite()
    {
        ManualEvidenceClock clock = new(
            new DateTimeOffset(2026, 7, 20, 18, 0, 0, TimeSpan.FromHours(8)));
        FakeEvidenceCapture capture = new();
        MemoryEvidenceSink sink = new(new DateOnly(2026, 7, 20));
        LegacyShutdownTaskEvidenceRuntime runtime = new(capture, sink, clock, ChinaTime);

        Assert.False(await runtime.CaptureIfDueAsync());
        Assert.Empty(capture.CheckedAt);
        Assert.Empty(sink.Documents);
    }

    [Fact]
    public async Task Runtime_UnexpectedCaptureFailureWritesFreshInconclusiveOnce()
    {
        ManualEvidenceClock clock = new(
            new DateTimeOffset(2026, 7, 20, 0, 12, 0, TimeSpan.FromHours(8)));
        FakeEvidenceCapture capture = new(throwOnCapture: true);
        MemoryEvidenceSink sink = new();
        LegacyShutdownTaskEvidenceRuntime runtime = new(capture, sink, clock, ChinaTime);

        Assert.True(await runtime.CaptureIfDueAsync());
        Assert.False(await runtime.CaptureIfDueAsync());
        Assert.Single(capture.CheckedAt);
        LegacyShutdownTaskEvidenceDocument document = Assert.Single(sink.Documents);
        Assert.Equal("inconclusive", document.Status);
        Assert.Equal("capture-failed", document.Error);
        Assert.Equal(clock.Now, document.CheckedAtLocal);
    }

    [Fact]
    public async Task Runtime_SinkFailureIsRateLimitedThenRetriesNextCycle()
    {
        ManualEvidenceClock clock = new(
            new DateTimeOffset(2026, 7, 20, 0, 12, 0, TimeSpan.FromHours(8)));
        FakeEvidenceCapture capture = new();
        MemoryEvidenceSink sink = new(failWrites: 1);
        LegacyShutdownTaskEvidenceRuntime runtime = new(capture, sink, clock, ChinaTime);

        Assert.True(await runtime.CaptureIfDueAsync());
        Assert.False(await runtime.CaptureIfDueAsync());
        clock.Now = clock.Now.AddMinutes(1);
        Assert.True(await runtime.CaptureIfDueAsync());

        Assert.Equal(2, capture.CheckedAt.Count);
        Assert.Single(sink.Documents);
    }

    [Fact]
    public async Task LocalAppDataSink_AtomicallyReplacesBoundedStrictJson()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "NightGate-evidence-tests",
            Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "legacy-shutdown-task-evidence.json");
        try
        {
            LocalLegacyShutdownTaskEvidenceSink sink = new(path);
            LegacyShutdownTaskEvidenceDocument first = Document(
                new DateTimeOffset(2026, 7, 20, 0, 11, 1, TimeSpan.FromHours(8)));
            LegacyShutdownTaskEvidenceDocument second = Document(
                new DateTimeOffset(2026, 7, 21, 0, 11, 2, TimeSpan.FromHours(8)));

            await sink.WriteAsync(first);
            await sink.WriteAsync(second);

            Assert.Equal(new DateOnly(2026, 7, 21), await sink.ReadLatestProbeDateAsync());
            using JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(
                "2026-07-21",
                json.RootElement.GetProperty("probeDateLocal").GetString());
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CurrentUserSinkFactory_FailsOpenWhenLocalAppDataIsUnavailable()
    {
        ILegacyShutdownTaskEvidenceSink sink =
            LocalLegacyShutdownTaskEvidenceSink.CreateForCurrentUser(
                () => throw new IOException("known folder unavailable"));

        Assert.IsType<UnavailableLegacyShutdownTaskEvidenceSink>(sink);
        Assert.Null(await sink.ReadLatestProbeDateAsync());
        await Assert.ThrowsAsync<IOException>(() =>
            sink.WriteAsync(Document(
                    new DateTimeOffset(
                        2026,
                        7,
                        20,
                        0,
                        11,
                        1,
                        TimeSpan.FromHours(8))))
                .AsTask());
    }

    [Fact]
    public async Task RuntimeFactory_EnvironmentalConstructionFailureCannotEscape()
    {
        ManualEvidenceClock clock = new(
            new DateTimeOffset(2026, 7, 20, 0, 12, 0, TimeSpan.FromHours(8)));

        LegacyShutdownTaskEvidenceRuntime runtime =
            LegacyShutdownTaskEvidenceRuntime.CreateForCurrentUser(
                new FakeLegacyMigrationService(Migration()),
                new FakeEvidenceReader(),
                clock,
                localTimeZone: () => throw new IOException("time zone unavailable"));

        Assert.True(await runtime.CaptureIfDueAsync());
        await runtime.StopAsync();
    }

    private static LegacyShutdownTaskEvidenceDocument Document(
        DateTimeOffset checkedAt) => new(
        SchemaVersion: 1,
        ProbeDateLocal: DateOnly.FromDateTime(checkedAt.DateTime),
        CheckedAtLocal: checkedAt,
        CheckedAtUtc: checkedAt.ToUniversalTime(),
        Status: "inconclusive",
        Error: "test",
        Tasks: []);

    private static DesktopLegacyTaskMigration Migration() => new(
        MigrationId,
        TaskPath,
        Fingerprint,
        OriginalEnabled: true,
        DesktopLegacyTaskMigrationStatus.Disabled,
        new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 7, 18, 12, 0, 1, TimeSpan.Zero),
        DisabledStateVerified: true);

    private static string ExpandWindows(string value) => value.Replace(
        "%SystemRoot%",
        @"C:\Windows",
        StringComparison.OrdinalIgnoreCase);

    private const string MigrationId = "migration-001";
    private const string TaskPath = @"\定时关机";
    private const string Fingerprint =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private sealed class EvidenceTaskPlatform(
        DateTimeOffset lastRunTimeUtc,
        int lastTaskResult) : ILegacyScheduledTaskPlatform
    {
        private readonly LegacyScheduledTaskSnapshot _snapshot = new(
            TaskPath,
            Enabled: false,
            [
                new LegacyScheduledTaskActionSnapshot(
                    LegacyScheduledTaskActionKind.Execute,
                    @"C:\Windows\System32\shutdown.exe",
                    "-s"),
            ],
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            lastRunTimeUtc,
            lastTaskResult);

        public int ReadCount { get; private set; }

        public int SetEnabledCount { get; private set; }

        public LegacyScheduledTaskEnumerationResult Enumerate(
            CancellationToken cancellationToken = default) => new(true, [_snapshot]);

        public LegacyScheduledTaskReadResult Read(
            string taskPath,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return LegacyScheduledTaskReadResult.Found(_snapshot);
        }

        public LegacyScheduledTaskSetEnabledStatus TrySetEnabled(
            LegacyScheduledTaskSnapshot expectedTask,
            bool enabled,
            CancellationToken cancellationToken = default)
        {
            SetEnabledCount++;
            return LegacyScheduledTaskSetEnabledStatus.Updated;
        }
    }

    private sealed class FakeEvidenceReader(
        params LegacyShutdownTaskRuntimeEvidence[] evidence) :
        ILegacyShutdownTaskEvidenceReader
    {
        public IReadOnlyList<LegacyShutdownTaskRuntimeEvidence> ReadRuntimeEvidence(
            IEnumerable<LegacyShutdownTaskCandidate>? candidates,
            CancellationToken cancellationToken = default) => evidence;
    }

    private sealed class FakeLegacyMigrationService : IDesktopLegacyMigrationService
    {
        private readonly DesktopLegacyMigrationListResult _result;

        public FakeLegacyMigrationService(
            DesktopLegacyTaskMigration? migration = null,
            string? error = null)
        {
            _result = error is null
                ? new(true, null, migration is null ? [] : [migration])
                : DesktopLegacyMigrationListResult.Unavailable(error);
        }

        public ValueTask<DesktopLegacyMigrationListResult> ListAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_result);

        public ValueTask<DesktopLegacyMigrationMutationResult> PrepareAsync(
            LegacyShutdownTaskCandidate candidate,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("evidence must not prepare migrations");

        public ValueTask<DesktopLegacyMigrationMutationResult> CompleteAsync(
            string migrationId,
            DesktopLegacyTaskMigrationStatus status,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("evidence must not complete migrations");

        public ValueTask<DesktopLegacyMigrationLookupResult> FindRecoveryCandidateAsync(
            string taskPath,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("evidence must not recover migrations");

        public ValueTask<DesktopLegacyMigrationMutationResult> RecoverDisabledAsync(
            DesktopLegacyTaskMigration migration,
            string recoveryToken,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("evidence must not recover migrations");
    }

    private sealed class FakeEvidenceCapture(bool throwOnCapture = false) :
        ILegacyShutdownTaskEvidenceCapture
    {
        public List<DateTimeOffset> CheckedAt { get; } = [];

        public ValueTask<LegacyShutdownTaskEvidenceDocument> CaptureAsync(
            DateTimeOffset checkedAtLocal,
            CancellationToken cancellationToken = default)
        {
            CheckedAt.Add(checkedAtLocal);
            if (throwOnCapture)
            {
                throw new IOException("capture failed");
            }

            return ValueTask.FromResult(Document(checkedAtLocal));
        }
    }

    private sealed class MemoryEvidenceSink(
        DateOnly? latest = null,
        int failWrites = 0) :
        ILegacyShutdownTaskEvidenceSink
    {
        public List<LegacyShutdownTaskEvidenceDocument> Documents { get; } = [];

        public ValueTask<DateOnly?> ReadLatestProbeDateAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(latest);

        public ValueTask WriteAsync(
            LegacyShutdownTaskEvidenceDocument document,
            CancellationToken cancellationToken = default)
        {
            if (failWrites > 0)
            {
                failWrites--;
                throw new IOException("sink failed");
            }

            Documents.Add(document);
            latest = document.ProbeDateLocal;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ManualEvidenceClock(DateTimeOffset now) : IDesktopRuntimeClock
    {
        public DateTimeOffset Now { get; set; } = now;

        public TimeSpan MonotonicNow => TimeSpan.Zero;

        public ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default) =>
            new(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
    }
}
