using System.Collections.Immutable;
using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class Win32ExactProcessActionAdapterTests
{
    private const Win32ProcessAccess CloseAccess =
        Win32ProcessAccess.QueryLimitedInformation | Win32ProcessAccess.Synchronize;
    private const Win32ProcessAccess TerminateAccess =
        CloseAccess | Win32ProcessAccess.Terminate;

    private static readonly DateTimeOffset Created = new(
        2026,
        7,
        6,
        23,
        59,
        59,
        TimeSpan.Zero);

    [Fact]
    public async Task Close_CollectsAllWindowsThenPostsOnlyEligibleWindows()
    {
        TestRig rig = TestRig.Create();
        nint eligible = 101;
        nint otherPid = 102;
        nint invisible = 103;
        nint disabled = 104;
        nint owned = 105;
        rig.Actions.Enumeration = Complete(
            eligible,
            otherPid,
            invisible,
            disabled,
            owned);
        rig.Actions.SetProbes(eligible, Eligible(), Eligible());
        rig.Actions.SetProbes(otherPid, Eligible(pid: 99));
        rig.Actions.SetProbes(invisible, Eligible(visible: false));
        rig.Actions.SetProbes(disabled, Eligible(enabled: false));
        rig.Actions.SetProbes(owned, Eligible(owner: 500));

        ProcessCloseOutcome outcome = await rig.Adapter.RequestCloseAsync(rig.Target);

        Assert.Equal(ProcessCloseOutcome.Requested, outcome);
        Assert.Equal(CloseAccess, Assert.Single(rig.Identity.OpenAccesses));
        Assert.Equal([eligible], rig.Actions.PostedWindows);
        Assert.Equal([Win32ExactProcessActionAdapter.WindowMessageClose], rig.Actions.PostedMessages);
        Assert.Equal(0, rig.Actions.TerminateCalls);
        Assert.True(rig.Actions.Events.IndexOf("enumeration-complete")
                    < rig.Actions.Events.IndexOf($"post:{eligible}"));
        Assert.All(rig.Actions.WaitHandles, handle => Assert.Same(rig.Identity.ProcessHandle, handle));
        Assert.Equal(rig.Actions.PostedWindows.Count, rig.Actions.PostHandlesAtCall.Count);
        Assert.All(rig.Actions.PostHandlesAtCall, handle => Assert.Same(rig.Identity.ProcessHandle, handle));
        Assert.True(rig.Identity.ProcessHandle.IsClosed);
        Assert.True(rig.Identity.TokenHandle.IsClosed);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("instant")]
    [InlineData("path")]
    [InlineData("sid")]
    [InlineData("session")]
    public async Task Close_IdentityMismatchStopsBeforeWindowEnumeration(string field)
    {
        TestRig rig = TestRig.Create();
        rig.Target = Mismatch(rig.Target, field);

        ProcessCloseOutcome outcome = await rig.Adapter.RequestCloseAsync(rig.Target);

        Assert.Equal(ProcessCloseOutcome.IdentityMismatch, outcome);
        Assert.Equal(0, rig.Actions.EnumerationCalls);
        Assert.Empty(rig.Actions.PostedWindows);
    }

    [Theory]
    [InlineData(Win32Error.FileNotFound, (int)ProcessCloseOutcome.TargetExited)]
    [InlineData(Win32Error.NotFound, (int)ProcessCloseOutcome.TargetExited)]
    [InlineData(Win32Error.AccessDenied, (int)ProcessCloseOutcome.Unavailable)]
    [InlineData(Win32Error.CallNotImplemented, (int)ProcessCloseOutcome.Unavailable)]
    [InlineData(999, (int)ProcessCloseOutcome.Ambiguous)]
    public async Task Close_MapsOpenFailuresWithoutEnumerating(
        int error,
        int expectedRaw)
    {
        TestRig rig = TestRig.Create();
        rig.Identity.OpenError = error;

        ProcessCloseOutcome outcome = await rig.Adapter.RequestCloseAsync(rig.Target);

        Assert.Equal((ProcessCloseOutcome)expectedRaw, outcome);
        Assert.Equal(0, rig.Actions.EnumerationCalls);
        Assert.Empty(rig.Actions.PostedWindows);
    }

    [Fact]
    public async Task Close_MapsSignalledIdentityHandleToTargetExited()
    {
        TestRig rig = TestRig.Create();
        rig.Identity.ReaderWaitResult = Win32ProcessWaitResult.Exited;

        ProcessCloseOutcome outcome = await rig.Adapter.RequestCloseAsync(rig.Target);

        Assert.Equal(ProcessCloseOutcome.TargetExited, outcome);
        Assert.Equal(0, rig.Actions.EnumerationCalls);
    }

    [Theory]
    [InlineData((int)Win32TopLevelWindowEnumerationStatus.Unavailable,
        (int)ProcessCloseOutcome.Unavailable)]
    [InlineData((int)Win32TopLevelWindowEnumerationStatus.Ambiguous,
        (int)ProcessCloseOutcome.Ambiguous)]
    public async Task Close_NonCompleteEnumerationNeverPostsOrClaimsNoWindow(
        int statusRaw,
        int expectedRaw)
    {
        TestRig rig = TestRig.Create();
        rig.Actions.Enumeration = new Win32TopLevelWindowEnumerationResult(
            (Win32TopLevelWindowEnumerationStatus)statusRaw,
            ImmutableArray.Create<nint>(101),
            Win32Error.InvalidData);

        ProcessCloseOutcome outcome = await rig.Adapter.RequestCloseAsync(rig.Target);

        Assert.Equal((ProcessCloseOutcome)expectedRaw, outcome);
        Assert.Empty(rig.Actions.PostedWindows);
        Assert.DoesNotContain(rig.Actions.Events, value => value.StartsWith("probe:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Close_CompleteNoWindowRequiresAStillAliveExactHandle()
    {
        TestRig rig = TestRig.Create();
        rig.Actions.Enumeration = Complete();

        ProcessCloseOutcome outcome = await rig.Adapter.RequestCloseAsync(rig.Target);

        Assert.Equal(ProcessCloseOutcome.NoEligibleWindow, outcome);
        Assert.Equal(2, rig.Actions.WaitHandles.Count);
        Assert.All(rig.Actions.WaitHandles, handle => Assert.Same(rig.Identity.ProcessHandle, handle));
        Assert.Empty(rig.Actions.PostedWindows);
    }

    [Theory]
    [InlineData((int)Win32ProcessWaitResult.Exited, (int)ProcessCloseOutcome.TargetExited)]
    [InlineData((int)Win32ProcessWaitResult.Failed, (int)ProcessCloseOutcome.Ambiguous)]
    public async Task Close_NoWindowDoesNotClaimNoWindowWhenFinalLivenessIsUncertain(
        int waitRaw,
        int expectedRaw)
    {
        TestRig rig = TestRig.Create();
        rig.Actions.Enumeration = Complete();
        rig.Actions.WaitResults.Enqueue(Win32ProcessWaitResult.Alive);
        rig.Actions.WaitResults.Enqueue((Win32ProcessWaitResult)waitRaw);

        ProcessCloseOutcome outcome = await rig.Adapter.RequestCloseAsync(rig.Target);

        Assert.Equal((ProcessCloseOutcome)expectedRaw, outcome);
        Assert.Empty(rig.Actions.PostedWindows);
    }

    [Fact]
    public async Task Close_TargetExitDuringEnumerationIsProvedOnTheOriginalHandle()
    {
        TestRig rig = TestRig.Create();
        rig.Actions.Enumeration = Complete(101);
        rig.Actions.SetProbes(101, Eligible(), Eligible());
        rig.Actions.WaitResults.Enqueue(Win32ProcessWaitResult.Exited);

        ProcessCloseOutcome outcome = await rig.Adapter.RequestCloseAsync(rig.Target);

        Assert.Equal(ProcessCloseOutcome.TargetExited, outcome);
        Assert.Empty(rig.Actions.PostedWindows);
        Assert.Same(rig.Identity.ProcessHandle, Assert.Single(rig.Actions.WaitHandles));
    }

    [Theory]
    [InlineData((int)Win32WindowProbeStatus.Unavailable,
        (int)ProcessCloseOutcome.Unavailable)]
    [InlineData((int)Win32WindowProbeStatus.Ambiguous,
        (int)ProcessCloseOutcome.Ambiguous)]
    public async Task Close_WindowProbeFailureIsNeverNoEligibleWindow(
        int probeStatusRaw,
        int expectedRaw)
    {
        TestRig rig = TestRig.Create();
        rig.Actions.Enumeration = Complete(101);
        rig.Actions.SetProbes(
            101,
            new Win32TopLevelWindowProbeResult(
                (Win32WindowProbeStatus)probeStatusRaw,
                default,
                Win32Error.InvalidHandle));

        ProcessCloseOutcome outcome = await rig.Adapter.RequestCloseAsync(rig.Target);

        Assert.Equal((ProcessCloseOutcome)expectedRaw, outcome);
        Assert.Empty(rig.Actions.PostedWindows);
    }

    [Theory]
    [InlineData(99, true, true, 0)]
    [InlineData(42, false, true, 0)]
    [InlineData(42, true, false, 0)]
    [InlineData(42, true, true, 900)]
    public async Task Close_RecheckRejectsStaleOrChangedEligibleWindow(
        int pid,
        bool visible,
        bool enabled,
        long owner)
    {
        TestRig rig = TestRig.Create();
        rig.Actions.Enumeration = Complete(101);
        rig.Actions.SetProbes(
            101,
            Eligible(),
            Eligible(pid, visible, enabled, (nint)owner));

        ProcessCloseOutcome outcome = await rig.Adapter.RequestCloseAsync(rig.Target);

        Assert.Equal(ProcessCloseOutcome.Ambiguous, outcome);
        Assert.Empty(rig.Actions.PostedWindows);
        Assert.Equal(2, rig.Actions.WaitHandles.Count);
    }

    [Fact]
    public async Task Close_RechecksTheSameHandleAndEachHwndImmediatelyBeforeEveryPost()
    {
        TestRig rig = TestRig.Create();
        rig.Actions.Enumeration = Complete(101, 102);
        rig.Actions.SetProbes(101, Eligible(), Eligible());
        rig.Actions.SetProbes(102, Eligible(), Eligible());

        ProcessCloseOutcome outcome = await rig.Adapter.RequestCloseAsync(rig.Target);

        Assert.Equal(ProcessCloseOutcome.Requested, outcome);
        Assert.Equal([101, 102], rig.Actions.PostedWindows);
        Assert.Equal(3, rig.Actions.WaitHandles.Count);
        Assert.All(rig.Actions.WaitHandles, handle => Assert.Same(rig.Identity.ProcessHandle, handle));
        Assert.Equal(
            ["wait", "probe:101", "post:101", "wait", "probe:102", "post:102"],
            rig.Actions.Events.Where(value =>
                    value == "wait"
                    || value.StartsWith("probe:", StringComparison.Ordinal)
                    || value.StartsWith("post:", StringComparison.Ordinal))
                .Skip(3));
    }

    [Fact]
    public async Task Close_UipiOrAnyPostFailureIsAmbiguous()
    {
        TestRig rig = TestRig.Create();
        rig.Actions.Enumeration = Complete(101);
        rig.Actions.SetProbes(101, Eligible(), Eligible());
        rig.Actions.PostReplies.Enqueue((false, Win32Error.AccessDenied));

        ProcessCloseOutcome outcome = await rig.Adapter.RequestCloseAsync(rig.Target);

        Assert.Equal(ProcessCloseOutcome.Ambiguous, outcome);
        Assert.Equal([101], rig.Actions.PostedWindows);
        Assert.Equal([Win32ExactProcessActionAdapter.WindowMessageClose], rig.Actions.PostedMessages);
    }

    [Fact]
    public async Task Close_PartialPostFailureIsAmbiguousAndNeverRetries()
    {
        TestRig rig = TestRig.Create();
        rig.Actions.Enumeration = Complete(101, 102);
        rig.Actions.SetProbes(101, Eligible(), Eligible());
        rig.Actions.SetProbes(102, Eligible(), Eligible());
        rig.Actions.PostReplies.Enqueue((true, Win32Error.Success));
        rig.Actions.PostReplies.Enqueue((false, Win32Error.AccessDenied));

        ProcessCloseOutcome outcome = await rig.Adapter.RequestCloseAsync(rig.Target);

        Assert.Equal(ProcessCloseOutcome.Ambiguous, outcome);
        Assert.Equal([101, 102], rig.Actions.PostedWindows);
    }

    [Fact]
    public async Task Close_ExitAfterSuccessfulPostIsRequested()
    {
        TestRig rig = TestRig.Create();
        rig.Actions.Enumeration = Complete(101, 102);
        rig.Actions.SetProbes(101, Eligible(), Eligible());
        rig.Actions.SetProbes(102, Eligible(), Eligible());
        rig.Actions.WaitResults.Enqueue(Win32ProcessWaitResult.Alive);
        rig.Actions.WaitResults.Enqueue(Win32ProcessWaitResult.Alive);
        rig.Actions.WaitResults.Enqueue(Win32ProcessWaitResult.Exited);

        ProcessCloseOutcome outcome = await rig.Adapter.RequestCloseAsync(rig.Target);

        Assert.Equal(ProcessCloseOutcome.Requested, outcome);
        Assert.Equal([101], rig.Actions.PostedWindows);
    }

    [Fact]
    public async Task Close_CancellationBeforeFirstSideEffectPropagates()
    {
        TestRig rig = TestRig.Create();
        rig.Actions.Enumeration = Complete(101);
        rig.Actions.SetProbes(101, Eligible(), Eligible());
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await rig.Adapter.RequestCloseAsync(rig.Target, cancellation.Token));

        Assert.Empty(rig.Actions.PostedWindows);
    }

    [Fact]
    public async Task Close_CancellationAfterTheLastLivenessCheckButBeforePostPropagates()
    {
        TestRig rig = TestRig.Create();
        rig.Actions.Enumeration = Complete(101);
        rig.Actions.SetProbes(101, Eligible(), Eligible());
        using CancellationTokenSource cancellation = new();
        int waitCount = 0;
        rig.Actions.AfterWait = _ =>
        {
            waitCount++;
            if (waitCount == 2)
            {
                cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await rig.Adapter.RequestCloseAsync(rig.Target, cancellation.Token));

        Assert.Empty(rig.Actions.PostedWindows);
    }

    [Fact]
    public async Task Close_CancellationAfterSuccessfulPostIsAmbiguous()
    {
        TestRig rig = TestRig.Create();
        rig.Actions.Enumeration = Complete(101, 102);
        rig.Actions.SetProbes(101, Eligible(), Eligible());
        rig.Actions.SetProbes(102, Eligible(), Eligible());
        using CancellationTokenSource cancellation = new();
        rig.Actions.AfterPost = _ => cancellation.Cancel();

        ProcessCloseOutcome outcome = await rig.Adapter.RequestCloseAsync(
            rig.Target,
            cancellation.Token);

        Assert.Equal(ProcessCloseOutcome.Ambiguous, outcome);
        Assert.Equal([101], rig.Actions.PostedWindows);
    }

    [Fact]
    public async Task Terminate_VerifiesAndUsesOneExactHandleForTheSingleCall()
    {
        TestRig rig = TestRig.Create();

        ProcessTerminationOutcome outcome =
            await rig.Adapter.RequestTerminationAsync(rig.Target);

        Assert.Equal(ProcessTerminationOutcome.Terminated, outcome);
        Assert.Equal(TerminateAccess, Assert.Single(rig.Identity.OpenAccesses));
        Assert.Equal(1, rig.Actions.TerminateCalls);
        Assert.Same(rig.Identity.ProcessHandle, Assert.Single(rig.Actions.TerminateHandles));
        Assert.Same(rig.Identity.ProcessHandle, Assert.Single(rig.Actions.WaitHandles));
        Assert.True(rig.Identity.ProcessHandle.IsClosed);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("instant")]
    [InlineData("path")]
    [InlineData("sid")]
    [InlineData("session")]
    public async Task Terminate_AnyIdentityMismatchCausesZeroSideEffects(string field)
    {
        TestRig rig = TestRig.Create();
        rig.Target = Mismatch(rig.Target, field);

        ProcessTerminationOutcome outcome =
            await rig.Adapter.RequestTerminationAsync(rig.Target);

        Assert.Equal(ProcessTerminationOutcome.IdentityMismatch, outcome);
        Assert.Equal(0, rig.Actions.TerminateCalls);
    }

    [Theory]
    [InlineData(Win32Error.FileNotFound, (int)ProcessTerminationOutcome.TargetExited)]
    [InlineData(Win32Error.NotFound, (int)ProcessTerminationOutcome.TargetExited)]
    [InlineData(Win32Error.AccessDenied, (int)ProcessTerminationOutcome.Unavailable)]
    [InlineData(Win32Error.NotSupported, (int)ProcessTerminationOutcome.Unavailable)]
    [InlineData(999, (int)ProcessTerminationOutcome.Ambiguous)]
    public async Task Terminate_IdentityReadFailureCausesZeroTerminationCalls(
        int error,
        int expectedRaw)
    {
        TestRig rig = TestRig.Create();
        rig.Identity.OpenError = error;

        ProcessTerminationOutcome outcome =
            await rig.Adapter.RequestTerminationAsync(rig.Target);

        Assert.Equal((ProcessTerminationOutcome)expectedRaw, outcome);
        Assert.Equal(0, rig.Actions.TerminateCalls);
    }

    [Theory]
    [InlineData((int)Win32ProcessWaitResult.Exited,
        (int)ProcessTerminationOutcome.TargetExited)]
    [InlineData((int)Win32ProcessWaitResult.Failed,
        (int)ProcessTerminationOutcome.Ambiguous)]
    public async Task Terminate_LastSameHandleLivenessCheckPreventsTheCall(
        int waitRaw,
        int expectedRaw)
    {
        TestRig rig = TestRig.Create();
        rig.Actions.WaitResults.Enqueue((Win32ProcessWaitResult)waitRaw);

        ProcessTerminationOutcome outcome =
            await rig.Adapter.RequestTerminationAsync(rig.Target);

        Assert.Equal((ProcessTerminationOutcome)expectedRaw, outcome);
        Assert.Equal(0, rig.Actions.TerminateCalls);
        Assert.Same(rig.Identity.ProcessHandle, Assert.Single(rig.Actions.WaitHandles));
    }

    [Fact]
    public async Task Terminate_CancellationImmediatelyBeforeTheUniqueCallPropagates()
    {
        TestRig rig = TestRig.Create();
        using CancellationTokenSource cancellation = new();
        rig.Actions.AfterWait = _ => cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await rig.Adapter.RequestTerminationAsync(rig.Target, cancellation.Token));

        Assert.Equal(0, rig.Actions.TerminateCalls);
    }

    [Theory]
    [InlineData(Win32Error.AccessDenied, (int)Win32ProcessWaitResult.Alive,
        (int)ProcessTerminationOutcome.Unavailable)]
    [InlineData(Win32Error.CallNotImplemented, (int)Win32ProcessWaitResult.Alive,
        (int)ProcessTerminationOutcome.Unavailable)]
    [InlineData(999, (int)Win32ProcessWaitResult.Alive,
        (int)ProcessTerminationOutcome.Ambiguous)]
    [InlineData(Win32Error.AccessDenied, (int)Win32ProcessWaitResult.Failed,
        (int)ProcessTerminationOutcome.Ambiguous)]
    [InlineData(999, (int)Win32ProcessWaitResult.Exited,
        (int)ProcessTerminationOutcome.TargetExited)]
    public async Task Terminate_FailureIsRecheckedOnceAndNeverRetried(
        int terminateError,
        int recheckRaw,
        int expectedRaw)
    {
        TestRig rig = TestRig.Create();
        rig.Actions.TerminateSucceeded = false;
        rig.Actions.TerminateError = terminateError;
        rig.Actions.WaitResults.Enqueue(Win32ProcessWaitResult.Alive);
        rig.Actions.WaitResults.Enqueue((Win32ProcessWaitResult)recheckRaw);

        ProcessTerminationOutcome outcome =
            await rig.Adapter.RequestTerminationAsync(rig.Target);

        Assert.Equal((ProcessTerminationOutcome)expectedRaw, outcome);
        Assert.Equal(1, rig.Actions.TerminateCalls);
        Assert.Equal(2, rig.Actions.WaitHandles.Count);
        Assert.All(rig.Actions.WaitHandles, handle => Assert.Same(rig.Identity.ProcessHandle, handle));
    }

    [Fact]
    public async Task Terminate_CancellationTriggeredByTheSuccessfulCallDoesNotRewriteTheEffect()
    {
        TestRig rig = TestRig.Create();
        using CancellationTokenSource cancellation = new();
        rig.Actions.AfterTerminate = () => cancellation.Cancel();

        ProcessTerminationOutcome outcome = await rig.Adapter.RequestTerminationAsync(
            rig.Target,
            cancellation.Token);

        Assert.Equal(ProcessTerminationOutcome.Terminated, outcome);
        Assert.Equal(1, rig.Actions.TerminateCalls);
    }

    [Theory]
    [InlineData(true, (int)ProcessCloseOutcome.Unavailable,
        (int)ProcessTerminationOutcome.Unavailable)]
    [InlineData(false, (int)ProcessCloseOutcome.Ambiguous,
        (int)ProcessTerminationOutcome.Ambiguous)]
    public async Task NativeExceptionsFailOpenAndAlwaysReleaseTheExactHandle(
        bool platformFailure,
        int expectedCloseRaw,
        int expectedTerminationRaw)
    {
        TestRig closeRig = TestRig.Create();
        closeRig.Actions.EnumerationException = platformFailure
            ? new DllNotFoundException()
            : new InvalidOperationException();

        ProcessCloseOutcome close =
            await closeRig.Adapter.RequestCloseAsync(closeRig.Target);

        TestRig terminationRig = TestRig.Create();
        terminationRig.Actions.TerminateException = platformFailure
            ? new EntryPointNotFoundException()
            : new InvalidOperationException();

        ProcessTerminationOutcome termination =
            await terminationRig.Adapter.RequestTerminationAsync(terminationRig.Target);

        Assert.Equal((ProcessCloseOutcome)expectedCloseRaw, close);
        Assert.Equal((ProcessTerminationOutcome)expectedTerminationRaw, termination);
        Assert.True(closeRig.Identity.ProcessHandle.IsClosed);
        Assert.True(terminationRig.Identity.ProcessHandle.IsClosed);
        Assert.Equal(1, terminationRig.Actions.TerminateCalls);
    }

    [Fact]
    public void ActionConstantsUseOnlyTheNarrowDocumentedSurface()
    {
        Assert.Equal(0x0010U, Win32ExactProcessActionAdapter.WindowMessageClose);
        Assert.Equal(1U, Win32ExactProcessActionAdapter.TerminationExitCode);
        Assert.InRange(
            Win32ExactProcessActionNative.MaximumTopLevelWindowCount,
            1,
            65_536);
    }

    private static Win32TopLevelWindowEnumerationResult Complete(params nint[] windows) =>
        new(
            Win32TopLevelWindowEnumerationStatus.Complete,
            windows.ToImmutableArray(),
            Win32Error.Success);

    private static Win32TopLevelWindowProbeResult Eligible(
        int pid = 42,
        bool visible = true,
        bool enabled = true,
        nint owner = default) =>
        new(
            Win32WindowProbeStatus.Success,
            new Win32TopLevelWindowState(pid, visible, enabled, owner),
            Win32Error.Success);

    private static ProcessExactTarget Mismatch(ProcessExactTarget target, string field) =>
        field switch
        {
            "key" => target with
            {
                InstanceKey = new ProcessInstanceKey(
                    target.InstanceKey.Pid,
                    target.InstanceKey.CreationUtcTicks + 1),
            },
            "instant" => target with
            {
                CreationInstantUtc = target.CreationInstantUtc.AddTicks(1),
            },
            "path" => target with { ExecutablePath = @"C:\Games\other.exe" },
            "sid" => target with { UserSid = "S-1-5-21-2000" },
            "session" => target with { SessionId = target.SessionId + 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

    private sealed class TestRig
    {
        private TestRig(
            FakeIdentityNative identity,
            FakeExactActionNative actions,
            Win32ExactProcessActionAdapter adapter,
            ProcessExactTarget target)
        {
            Identity = identity;
            Actions = actions;
            Adapter = adapter;
            Target = target;
        }

        public FakeIdentityNative Identity { get; }

        public FakeExactActionNative Actions { get; }

        public Win32ExactProcessActionAdapter Adapter { get; }

        public ProcessExactTarget Target { get; set; }

        public static TestRig Create()
        {
            ProcessExactTarget target = new(
                new ProcessInstanceKey(42, Created.UtcTicks),
                Created,
                @"C:\Games\game.exe",
                "S-1-5-21-1000",
                7,
                "game",
                Created.AddMinutes(5),
                new DateOnly(2026, 7, 6),
                "rule-fingerprint",
                11,
                "policy-11",
                "payload-11",
                Created);
            FakeIdentityNative identity = new(target);
            FakeExactActionNative actions = new();
            Win32ExactProcessActionAdapter adapter = new(
                new Win32ProcessIdentityReader(identity),
                actions);
            return new TestRig(identity, actions, adapter, target);
        }
    }

    private sealed class FakeIdentityNative : IWin32ProcessIdentityNative
    {
        private readonly ProcessExactTarget _target;

        public FakeIdentityNative(ProcessExactTarget target)
        {
            _target = target;
            ProcessHandle = new SafeWin32ProcessHandle((nint)7001, ownsHandle: false);
            TokenHandle = new SafeWin32TokenHandle((nint)7002, ownsHandle: false);
        }

        public SafeWin32ProcessHandle ProcessHandle { get; }

        public SafeWin32TokenHandle TokenHandle { get; }

        public List<Win32ProcessAccess> OpenAccesses { get; } = [];

        public int OpenError { get; set; }

        public Win32ProcessWaitResult ReaderWaitResult { get; set; } =
            Win32ProcessWaitResult.Alive;

        public SafeWin32ProcessHandle? OpenProcess(
            int pid,
            Win32ProcessAccess access,
            out int error)
        {
            OpenAccesses.Add(access);
            error = OpenError;
            return error == Win32Error.Success ? ProcessHandle : null;
        }

        public bool TryGetProcessId(
            SafeWin32ProcessHandle process,
            out int pid,
            out int error)
        {
            pid = _target.InstanceKey.Pid;
            error = Win32Error.Success;
            return true;
        }

        public bool TryGetCreationFileTime(
            SafeWin32ProcessHandle process,
            out long creationFileTimeUtc,
            out int error)
        {
            creationFileTimeUtc = _target.CreationInstantUtc.UtcDateTime.ToFileTimeUtc();
            error = Win32Error.Success;
            return true;
        }

        public Win32StringCallResult QueryFullProcessImageName(
            SafeWin32ProcessHandle process,
            int capacity) => new(true, _target.ExecutablePath, Win32Error.Success);

        public SafeWin32TokenHandle? OpenProcessToken(
            SafeWin32ProcessHandle process,
            Win32TokenAccess access,
            out int error)
        {
            error = Win32Error.Success;
            return TokenHandle;
        }

        public bool TryGetTokenUserSid(
            SafeWin32TokenHandle token,
            out string sid,
            out int error)
        {
            sid = _target.UserSid;
            error = Win32Error.Success;
            return true;
        }

        public bool TryGetTokenSessionId(
            SafeWin32TokenHandle token,
            out uint sessionId,
            out int error)
        {
            sessionId = checked((uint)_target.SessionId);
            error = Win32Error.Success;
            return true;
        }

        public Win32ProcessWaitResult WaitForProcess(
            SafeWin32ProcessHandle process,
            out int error)
        {
            error = Win32Error.Success;
            return ReaderWaitResult;
        }
    }

    private sealed class FakeExactActionNative : IWin32ExactProcessActionNative
    {
        private readonly Dictionary<nint, Queue<Win32TopLevelWindowProbeResult>> _probes = [];
        private SafeWin32ProcessHandle? _lastWaitHandle;

        public Win32TopLevelWindowEnumerationResult Enumeration { get; set; } = Complete();

        public Exception? EnumerationException { get; set; }

        public Exception? TerminateException { get; set; }

        public int EnumerationCalls { get; private set; }

        public Queue<Win32ProcessWaitResult> WaitResults { get; } = [];

        public Queue<(bool Succeeded, int Error)> PostReplies { get; } = [];

        public List<SafeWin32ProcessHandle> WaitHandles { get; } = [];

        public List<nint> PostedWindows { get; } = [];

        public List<uint> PostedMessages { get; } = [];

        public List<SafeWin32ProcessHandle> PostHandlesAtCall { get; } = [];

        public List<SafeWin32ProcessHandle> TerminateHandles { get; } = [];

        public List<string> Events { get; } = [];

        public Action<nint>? AfterPost { get; set; }

        public Action<SafeWin32ProcessHandle>? AfterWait { get; set; }

        public Action? AfterTerminate { get; set; }

        public bool TerminateSucceeded { get; set; } = true;

        public int TerminateError { get; set; }

        public int TerminateCalls { get; private set; }

        public void SetProbes(
            nint window,
            params Win32TopLevelWindowProbeResult[] replies) =>
            _probes[window] = new Queue<Win32TopLevelWindowProbeResult>(replies);

        public Win32TopLevelWindowEnumerationResult EnumerateTopLevelWindows()
        {
            EnumerationCalls++;
            if (EnumerationException is not null)
            {
                throw EnumerationException;
            }

            Events.Add("enumeration-complete");
            return Enumeration;
        }

        public Win32TopLevelWindowProbeResult ProbeTopLevelWindow(nint window)
        {
            Events.Add($"probe:{window}");
            if (!_probes.TryGetValue(window, out Queue<Win32TopLevelWindowProbeResult>? replies)
                || replies.Count == 0)
            {
                return new(
                    Win32WindowProbeStatus.Ambiguous,
                    default,
                    Win32Error.InvalidData);
            }

            return replies.Dequeue();
        }

        public Win32ProcessWaitResult WaitForProcess(
            SafeWin32ProcessHandle process,
            out int error)
        {
            WaitHandles.Add(process);
            _lastWaitHandle = process;
            Events.Add("wait");
            Win32ProcessWaitResult result = WaitResults.TryDequeue(
                out Win32ProcessWaitResult queued)
                ? queued
                : Win32ProcessWaitResult.Alive;
            error = result == Win32ProcessWaitResult.Failed
                ? Win32Error.InvalidHandle
                : Win32Error.Success;
            AfterWait?.Invoke(process);
            return result;
        }

        public bool TryPostMessage(
            nint window,
            uint message,
            nuint wParam,
            nint lParam,
            out int error)
        {
            PostedWindows.Add(window);
            PostedMessages.Add(message);
            if (_lastWaitHandle is not null)
            {
                PostHandlesAtCall.Add(_lastWaitHandle);
            }
            Events.Add($"post:{window}");
            (bool succeeded, int nativeError) = PostReplies.TryDequeue(
                out (bool Succeeded, int Error) queued)
                ? queued
                : (true, Win32Error.Success);
            error = nativeError;
            AfterPost?.Invoke(window);
            return succeeded;
        }

        public bool TryTerminate(
            SafeWin32ProcessHandle process,
            uint exitCode,
            out int error)
        {
            TerminateCalls++;
            TerminateHandles.Add(process);
            if (TerminateException is not null)
            {
                throw TerminateException;
            }

            error = TerminateError;
            AfterTerminate?.Invoke();
            return TerminateSucceeded;
        }
    }
}
