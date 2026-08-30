using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class RunningGameDetectorTests
{
    private const string GamePath = @"C:\Games\Example\game.exe";
    private const string UserSid = "S-1-5-21-100-200-300-1001";
    private static readonly CurrentInteractiveIdentity InteractiveIdentity = new(UserSid, 1);

    [Fact]
    public async Task ConfiguredGameInCurrentAccountAndSessionIsDetected()
    {
        FakeCatalog native = new(new Win32ProcessCatalogEntry(41, 0, "game.exe"));
        FakeIdentities identities = new(Identity(41, GamePath));
        WindowsRunningGameDetector detector = new(native, identities);

        Assert.True(await detector.HasRunningGameAsync([GameRule()], InteractiveIdentity));
        Assert.Equal([41], identities.ReadPids);
        Assert.True(native.Snapshot.IsClosed);
    }

    [Fact]
    public async Task PathsAndExecutableNamesAreCanonicalizedCaseInsensitively()
    {
        FakeCatalog native = new(new Win32ProcessCatalogEntry(41, 0, "GAME.EXE"));
        FakeIdentities identities = new(Identity(41, @"\\?\c:\games\example\GAME.exe"));
        WindowsRunningGameDetector detector = new(native, identities);

        Assert.True(await detector.HasRunningGameAsync(
            [GameRule() with { RootExecutablePath = "C:/Games/Example/./game.exe" }],
            InteractiveIdentity));
    }

    [Theory]
    [InlineData(DesktopAppRuleCategory.Voice, true)]
    [InlineData(DesktopAppRuleCategory.Game, false)]
    [InlineData(null, true)]
    public async Task UnconfiguredOrNonGameRulesDoNotStartAProcessScan(
        DesktopAppRuleCategory? category,
        bool configured)
    {
        FakeCatalog native = new(new Win32ProcessCatalogEntry(41, 0, "game.exe"));
        FakeIdentities identities = new(Identity(41, GamePath));
        WindowsRunningGameDetector detector = new(native, identities);

        Assert.False(await detector.HasRunningGameAsync(
            [GameRule() with { Category = category, IsConfigured = configured }],
            InteractiveIdentity));
        Assert.Equal(0, native.CreateCalls);
        Assert.Empty(identities.ReadPids);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("game.exe")]
    [InlineData(@"C:\Games\game.dll")]
    public async Task InvalidRootPathIsNotDetected(string? path)
    {
        FakeCatalog native = new();
        WindowsRunningGameDetector detector = new(native, new FakeIdentities());

        Assert.False(await detector.HasRunningGameAsync(
            [GameRule() with { RootExecutablePath = path }], InteractiveIdentity));
        Assert.Equal(0, native.CreateCalls);
    }

    [Fact]
    public async Task HelperVoiceAndUnrelatedNamesAreFilteredBeforeIdentityQueries()
    {
        FakeCatalog native = new(
            new Win32ProcessCatalogEntry(41, 0, "helper.exe"),
            new Win32ProcessCatalogEntry(42, 0, "voice.exe"),
            new Win32ProcessCatalogEntry(43, 0, "other.exe"));
        FakeIdentities identities = new();
        WindowsRunningGameDetector detector = new(native, identities);

        Assert.False(await detector.HasRunningGameAsync([
            GameRule(),
            GameRule() with { Category = DesktopAppRuleCategory.Voice, RootExecutablePath = @"C:\Voice\voice.exe" },
        ], InteractiveIdentity));
        Assert.Empty(identities.ReadPids);
        Assert.True(native.Snapshot.IsClosed);
    }

    [Theory]
    [InlineData(@"C:\Other\game.exe", UserSid, 1)]
    [InlineData(GamePath, "S-1-5-21-100-200-300-1002", 1)]
    [InlineData(GamePath, UserSid, 2)]
    public async Task SameNameWrongDirectoryOrOtherAccountOrSessionIsNotDetected(
        string path,
        string sid,
        int session)
    {
        FakeCatalog native = new(new Win32ProcessCatalogEntry(41, 0, "game.exe"));
        FakeIdentities identities = new(Identity(41, path, sid, session));
        WindowsRunningGameDetector detector = new(native, identities);

        Assert.False(await detector.HasRunningGameAsync([GameRule()], InteractiveIdentity));
        Assert.Equal([41], identities.ReadPids);
    }

    [Fact]
    public async Task EmptyRuleListAvoidsSnapshotCreation()
    {
        FakeCatalog native = new();
        WindowsRunningGameDetector detector = new(native, new FakeIdentities());

        Assert.False(await detector.HasRunningGameAsync([], InteractiveIdentity));
        Assert.Equal(0, native.CreateCalls);
    }

    [Fact]
    public async Task ExitedOrUnreadableCandidateDoesNotPreventFindingAnotherLiveGame()
    {
        FakeCatalog native = new(
            new Win32ProcessCatalogEntry(41, 0, "game.exe"),
            new Win32ProcessCatalogEntry(42, 0, "game.exe"));
        FakeIdentities identities = new(Identity(42, GamePath));
        WindowsRunningGameDetector detector = new(native, identities);

        Assert.True(await detector.HasRunningGameAsync([GameRule()], InteractiveIdentity));
        Assert.Equal([41, 42], identities.ReadPids);
    }

    [Theory]
    [InlineData("Exited")]
    [InlineData("NotFound")]
    [InlineData("AccessDenied")]
    [InlineData("Unavailable")]
    [InlineData("Ambiguous")]
    public async Task NonLiveOrUnverifiedIdentityIsNotDetected(string status)
    {
        FakeCatalog native = new(new Win32ProcessCatalogEntry(41, 0, "game.exe"));
        FakeIdentities identities = new() { MissingStatus = Enum.Parse<Win32ProcessIdentityReadStatus>(status) };
        WindowsRunningGameDetector detector = new(native, identities);

        Assert.False(await detector.HasRunningGameAsync([GameRule()], InteractiveIdentity));
        Assert.True(native.Snapshot.IsClosed);
    }

    [Fact]
    public async Task ReusedOrMismatchedPidEvidenceIsIgnored()
    {
        FakeCatalog native = new(new Win32ProcessCatalogEntry(41, 0, "game.exe"));
        FakeIdentities identities = new() { ForcedIdentity = Identity(99, GamePath) };
        WindowsRunningGameDetector detector = new(native, identities);

        Assert.False(await detector.HasRunningGameAsync([GameRule()], InteractiveIdentity));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("enumerate")]
    [InlineData("identity")]
    public async Task NativeExceptionsFailQuietlyWithoutLeavingSnapshotOpen(string failure)
    {
        FakeCatalog native = new(new Win32ProcessCatalogEntry(41, 0, "game.exe"))
        {
            ThrowOnCreate = failure == "create",
            ThrowOnNext = failure == "enumerate",
        };
        FakeIdentities identities = new(Identity(41, GamePath)) { ThrowOnRead = failure == "identity" };
        WindowsRunningGameDetector detector = new(native, identities);

        Assert.False(await detector.HasRunningGameAsync([GameRule()], InteractiveIdentity));
        if (failure != "create")
        {
            Assert.True(native.Snapshot.IsClosed);
        }
    }

    [Fact]
    public async Task FailedOrIncompleteEnumerationDoesNotPublishAnEarlierCandidate()
    {
        FakeCatalog native = new(new Win32ProcessCatalogEntry(41, 0, "game.exe")) { FailAfterEntries = true };
        FakeIdentities identities = new(Identity(41, GamePath));
        WindowsRunningGameDetector detector = new(native, identities);

        Assert.False(await detector.HasRunningGameAsync([GameRule()], InteractiveIdentity));
        Assert.Empty(identities.ReadPids);
        Assert.True(native.Snapshot.IsClosed);
    }

    [Fact]
    public async Task MissingSnapshotFailsQuietlyWithoutIdentityQueries()
    {
        FakeCatalog native = new() { MissingSnapshot = true };
        FakeIdentities identities = new();
        WindowsRunningGameDetector detector = new(native, identities);

        Assert.False(await detector.HasRunningGameAsync([GameRule()], InteractiveIdentity));
        Assert.Empty(identities.ReadPids);
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData(UserSid, -1)]
    public async Task InvalidInteractiveIdentityAvoidsProcessScan(string sid, int session)
    {
        FakeCatalog native = new();
        WindowsRunningGameDetector detector = new(native, new FakeIdentities());

        Assert.False(await detector.HasRunningGameAsync([GameRule()], new(sid, session)));
        Assert.Equal(0, native.CreateCalls);
    }

    [Fact]
    public async Task EndlessEnumerationStopsAtTheRowLimitAndDisposesSnapshot()
    {
        FakeCatalog native = new() { RepeatEntry = new(41, 0, "game.exe") };
        FakeIdentities identities = new(Identity(41, GamePath));
        WindowsRunningGameDetector detector = new(native, identities);

        Assert.False(await detector.HasRunningGameAsync([GameRule()], InteractiveIdentity));
        Assert.Equal(WindowsRunningGameDetector.MaximumSnapshotRows + 1, native.MoveCalls);
        Assert.Empty(identities.ReadPids);
        Assert.True(native.Snapshot.IsClosed);
    }

    [Fact]
    public async Task CancellationBeforeStartDoesNotCreateSnapshot()
    {
        FakeCatalog native = new();
        WindowsRunningGameDetector detector = new(native, new FakeIdentities());
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            detector.HasRunningGameAsync([GameRule()], InteractiveIdentity, cancellation.Token).AsTask());
        Assert.Equal(0, native.CreateCalls);
    }

    [Fact]
    public async Task CancellationDuringEnumerationIsPropagatedAndDisposesSnapshot()
    {
        using CancellationTokenSource cancellation = new();
        FakeCatalog native = new(new Win32ProcessCatalogEntry(41, 0, "game.exe"))
        {
            BeforeFirst = cancellation.Cancel,
        };
        WindowsRunningGameDetector detector = new(native, new FakeIdentities());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            detector.HasRunningGameAsync([GameRule()], InteractiveIdentity, cancellation.Token).AsTask());
        Assert.True(native.Snapshot.IsClosed);
    }

    [Fact]
    public async Task CancellationDuringIdentityReadIsNotConvertedToPositiveDetection()
    {
        using CancellationTokenSource cancellation = new();
        FakeCatalog native = new(new Win32ProcessCatalogEntry(41, 0, "game.exe"));
        FakeIdentities identities = new(Identity(41, GamePath)) { BeforeRead = cancellation.Cancel };
        WindowsRunningGameDetector detector = new(native, identities);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            detector.HasRunningGameAsync([GameRule()], InteractiveIdentity, cancellation.Token).AsTask());
        Assert.True(native.Snapshot.IsClosed);
    }

    [Fact]
    public async Task CatalogRunsOnBackgroundThreadRatherThanCallerThread()
    {
        FakeCatalog native = new(new Win32ProcessCatalogEntry(41, 0, "game.exe"));
        WindowsRunningGameDetector detector = new(native, new FakeIdentities(Identity(41, GamePath)));
        TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int callerThread = 0;
        Thread caller = new(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            try
            {
                completion.SetResult(detector.HasRunningGameAsync([GameRule()], InteractiveIdentity)
                    .AsTask().GetAwaiter().GetResult());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        caller.Start();

        Assert.True(await completion.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.NotEqual(callerThread, native.CreateThread);
    }

    [Fact]
    [Trait("Category", "WindowsSmoke")]
    public async Task ProductionDetectorCanObserveCurrentProcessWithoutMutation()
    {
        CurrentInteractiveIdentity identity = Assert.IsType<CurrentInteractiveIdentity>(
            new WindowsCurrentInteractiveIdentityProvider().Read());
        DesktopAppRuleDto currentProcessRule = GameRule() with { RootExecutablePath = Environment.ProcessPath };

        Assert.True(await new WindowsRunningGameDetector().HasRunningGameAsync([currentProcessRule], identity));
    }

    private static DesktopAppRuleDto GameRule() => new(
        "example", GamePath, [@"C:\Games\Example\helper.exe"],
        DesktopAppRuleCategory.Game, 35, true);

    private static ObservedProcessIdentity Identity(
        int pid,
        string path,
        string sid = UserSid,
        int session = 1)
    {
        DateTimeOffset created = new(2026, 8, 30, 13, 0, 0, TimeSpan.Zero);
        return new(new(pid, created.UtcTicks), created, path, sid, session);
    }

    private sealed class FakeCatalog(params Win32ProcessCatalogEntry[] entries)
        : IWin32ProcessCatalogNative
    {
        private int _index;

        internal SafeWin32ProcessSnapshotHandle Snapshot { get; } = new((nint)123, ownsHandle: false);

        internal int CreateCalls { get; private set; }
        internal int CreateThread { get; private set; }
        internal int MoveCalls { get; private set; }
        internal bool ThrowOnCreate { get; init; }
        internal bool MissingSnapshot { get; init; }
        internal bool ThrowOnNext { get; init; }
        internal bool FailAfterEntries { get; init; }
        internal Action? BeforeFirst { get; init; }
        internal Win32ProcessCatalogEntry? RepeatEntry { get; init; }

        public SafeWin32ProcessSnapshotHandle? CreateProcessSnapshot(out int error)
        {
            CreateCalls++;
            CreateThread = Environment.CurrentManagedThreadId;
            if (ThrowOnCreate)
            {
                throw new InvalidOperationException("Snapshot unavailable.");
            }

            error = MissingSnapshot ? Win32Error.InvalidHandle : Win32Error.Success;
            return MissingSnapshot ? null : Snapshot;
        }

        public Win32ProcessCatalogMoveResult ReadFirst(SafeWin32ProcessSnapshotHandle snapshot)
        {
            BeforeFirst?.Invoke();
            _index = 0;
            return ReadNext(snapshot);
        }

        public Win32ProcessCatalogMoveResult ReadNext(SafeWin32ProcessSnapshotHandle snapshot)
        {
            MoveCalls++;
            if (ThrowOnNext)
            {
                throw new InvalidOperationException("Enumeration unavailable.");
            }

            if (RepeatEntry is { } repeated)
            {
                return Win32ProcessCatalogMoveResult.Entry(repeated);
            }

            return _index < entries.Length
                ? Win32ProcessCatalogMoveResult.Entry(entries[_index++])
                : FailAfterEntries
                    ? Win32ProcessCatalogMoveResult.Failure(Win32Error.InvalidData)
                    : Win32ProcessCatalogMoveResult.Completed();
        }
    }

    private sealed class FakeIdentities(params ObservedProcessIdentity[] identities)
        : IProcessCatalogIdentityReader
    {
        internal List<int> ReadPids { get; } = [];
        internal bool ThrowOnRead { get; init; }
        internal Action? BeforeRead { get; init; }
        internal Win32ProcessIdentityReadStatus MissingStatus { get; init; } = Win32ProcessIdentityReadStatus.Exited;
        internal ObservedProcessIdentity? ForcedIdentity { get; init; }

        public ProcessCatalogIdentityReadResult Read(int pid)
        {
            ReadPids.Add(pid);
            BeforeRead?.Invoke();
            if (ThrowOnRead)
            {
                throw new InvalidOperationException("Identity unavailable.");
            }

            ObservedProcessIdentity? identity = ForcedIdentity
                ?? identities.SingleOrDefault(value => value.Key.Pid == pid);
            return identity is null
                ? ProcessCatalogIdentityReadResult.Failure(MissingStatus)
                : ProcessCatalogIdentityReadResult.Success(identity);
        }
    }
}
