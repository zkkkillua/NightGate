using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class Win32ProcessIdentityReaderTests
{
    private const Win32ProcessAccess RequiredAccess =
        Win32ProcessAccess.QueryLimitedInformation | Win32ProcessAccess.Synchronize;

    private static readonly DateTimeOffset Created = new DateTimeOffset(
        2026,
        7,
        6,
        23,
        59,
        59,
        TimeSpan.Zero).AddTicks(7);

    [Fact]
    public void OpenAndRead_UsesOneLiveHandleAndPreservesFileTimePrecision()
    {
        FakeIdentityNative native = FakeIdentityNative.Success(Created);
        Win32ProcessIdentityReader reader = new(native);

        using Win32ProcessIdentityReadResult result = reader.OpenAndRead(
            42,
            Win32ProcessAccess.QueryLimitedInformation | Win32ProcessAccess.Synchronize);

        Assert.Equal(Win32ProcessIdentityReadStatus.Success, result.Status);
        Assert.NotNull(result.Handle);
        ObservedProcessIdentity identity = Assert.IsType<ObservedProcessIdentity>(result.Identity);
        Assert.Equal(new ProcessInstanceKey(42, Created.UtcTicks), identity.Key);
        Assert.Equal(Created, identity.CreationInstantUtc);
        Assert.Equal(@"C:\Games\game.exe", identity.ExecutablePath, ignoreCase: true);
        Assert.Equal("S-1-5-21-1000", identity.UserSid);
        Assert.Equal(7, identity.SessionId);
        Assert.Equal(
            Win32ProcessAccess.QueryLimitedInformation | Win32ProcessAccess.Synchronize,
            native.OpenAccesses.Single());
        Assert.All(native.ProcessHandleUses, handle => Assert.Same(result.Handle, handle));
        Assert.Equal(Win32TokenAccess.Query, Assert.Single(native.TokenAccesses));
        Assert.Equal(Win32ProcessWaitResult.Alive, native.WaitResult);
        Assert.False(native.ProcessHandle.IsClosed);
        Assert.True(native.TokenHandle.IsClosed);
    }

    [Fact]
    public void OpenAndRead_RetriesImageBufferWithinTheBoundAndCanonicalizesExtendedPath()
    {
        FakeIdentityNative native = FakeIdentityNative.Success(Created);
        native.PathReplies.Enqueue(new(false, null, Win32Error.InsufficientBuffer));
        native.PathReplies.Enqueue(new(true, @"\\?\C:\Games\game.exe", Win32Error.Success));

        using Win32ProcessIdentityReadResult result = new Win32ProcessIdentityReader(native)
            .OpenAndRead(
                42,
                Win32ProcessAccess.QueryLimitedInformation | Win32ProcessAccess.Synchronize);

        Assert.Equal(Win32ProcessIdentityReadStatus.Success, result.Status);
        Assert.Equal([260, 520], native.PathCapacities);
        Assert.Equal(@"C:\Games\game.exe", result.Identity!.ExecutablePath, ignoreCase: true);
    }

    [Fact]
    public void SamePidWithAnotherCreationFileTimeProducesAnotherExactKey()
    {
        FakeIdentityNative firstNative = FakeIdentityNative.Success(Created);
        FakeIdentityNative secondNative = FakeIdentityNative.Success(Created.AddTicks(1));

        using Win32ProcessIdentityReadResult first = new Win32ProcessIdentityReader(firstNative)
            .OpenAndRead(
                42,
                Win32ProcessAccess.QueryLimitedInformation | Win32ProcessAccess.Synchronize);
        using Win32ProcessIdentityReadResult second = new Win32ProcessIdentityReader(secondNative)
            .OpenAndRead(
                42,
                Win32ProcessAccess.QueryLimitedInformation | Win32ProcessAccess.Synchronize);

        Assert.Equal(Win32ProcessIdentityReadStatus.Success, first.Status);
        Assert.Equal(Win32ProcessIdentityReadStatus.Success, second.Status);
        Assert.NotEqual(first.Identity!.Key, second.Identity!.Key);
    }

    [Fact]
    public void ZeroFileTimeUsesTheExactWindowsEpochWithoutLocalTimeArithmetic()
    {
        FakeIdentityNative native = FakeIdentityNative.Success(Created);
        native.CreationFileTimeUtc = 0;

        using Win32ProcessIdentityReadResult result = new Win32ProcessIdentityReader(native)
            .OpenAndRead(42, RequiredAccess);

        Assert.Equal(Win32ProcessIdentityReadStatus.Success, result.Status);
        DateTimeOffset expected = new(
            new DateTime(DateTime.FromFileTimeUtc(0).Ticks, DateTimeKind.Utc));
        Assert.Equal(expected, result.Identity!.CreationInstantUtc);
        Assert.Equal(expected.UtcTicks, result.Identity.Key.CreationUtcTicks);
    }

    [Fact]
    public void FileTimeUpperBoundaryIsAcceptedAndTheNextTickIsRejected()
    {
        DateTime maximumUtc = new(DateTime.MaxValue.Ticks, DateTimeKind.Utc);
        long maximumFileTime = maximumUtc.ToFileTimeUtc();
        FakeIdentityNative boundaryNative = FakeIdentityNative.Success(Created);
        boundaryNative.CreationFileTimeUtc = maximumFileTime;
        FakeIdentityNative overflowNative = FakeIdentityNative.Success(Created);
        overflowNative.CreationFileTimeUtc = maximumFileTime + 1;

        using Win32ProcessIdentityReadResult boundary =
            new Win32ProcessIdentityReader(boundaryNative)
                .OpenAndRead(42, RequiredAccess);
        using Win32ProcessIdentityReadResult overflow =
            new Win32ProcessIdentityReader(overflowNative)
                .OpenAndRead(42, RequiredAccess);

        Assert.Equal(Win32ProcessIdentityReadStatus.Success, boundary.Status);
        Assert.Equal(maximumUtc.Ticks, boundary.Identity!.CreationInstantUtc.UtcTicks);
        Assert.Equal(Win32ProcessIdentityReadStatus.Ambiguous, overflow.Status);
        Assert.True(overflowNative.ProcessHandle.IsClosed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void NonPositivePidIsRejectedBeforeAnyNativeCall(int pid)
    {
        FakeIdentityNative native = FakeIdentityNative.Success(Created);

        using Win32ProcessIdentityReadResult result = new Win32ProcessIdentityReader(native)
            .OpenAndRead(pid, RequiredAccess);

        Assert.Equal(Win32ProcessIdentityReadStatus.Ambiguous, result.Status);
        Assert.Empty(native.OpenAccesses);
    }

    [Theory]
    [InlineData((uint)Win32ProcessAccess.None)]
    [InlineData((uint)Win32ProcessAccess.QueryLimitedInformation)]
    [InlineData((uint)Win32ProcessAccess.Synchronize)]
    [InlineData((uint)Win32ProcessAccess.Terminate)]
    [InlineData(0x00000010U)]
    [InlineData(uint.MaxValue)]
    public void InvalidOrOverwideAccessIsRejectedBeforeAnyNativeCall(uint rawAccess)
    {
        FakeIdentityNative native = FakeIdentityNative.Success(Created);
        Win32ProcessAccess access = (Win32ProcessAccess)rawAccess;

        using Win32ProcessIdentityReadResult result = new Win32ProcessIdentityReader(native)
            .OpenAndRead(42, access);

        Assert.Equal(Win32ProcessIdentityReadStatus.Ambiguous, result.Status);
        Assert.Empty(native.OpenAccesses);
    }

    [Fact]
    public void TerminateAccessIsAcceptedOnlyWithTheRequiredIdentityRights()
    {
        FakeIdentityNative native = FakeIdentityNative.Success(Created);
        Win32ProcessAccess access = RequiredAccess | Win32ProcessAccess.Terminate;

        using Win32ProcessIdentityReadResult result = new Win32ProcessIdentityReader(native)
            .OpenAndRead(42, access);

        Assert.Equal(Win32ProcessIdentityReadStatus.Success, result.Status);
        Assert.Equal(access, Assert.Single(native.OpenAccesses));
    }

    [Fact]
    public void NativeAccessConstantsMatchTheDocumentedMinimumRights()
    {
        Assert.Equal(0x00001000U, (uint)Win32ProcessAccess.QueryLimitedInformation);
        Assert.Equal(0x00100000U, (uint)Win32ProcessAccess.Synchronize);
        Assert.Equal(0x00000001U, (uint)Win32ProcessAccess.Terminate);
        Assert.Equal(0x00000008U, (uint)Win32TokenAccess.Query);
    }

    [Theory]
    [InlineData(Win32Error.AccessDenied, (int)Win32ProcessIdentityReadStatus.AccessDenied)]
    [InlineData(Win32Error.InvalidParameter, (int)Win32ProcessIdentityReadStatus.NotFound)]
    [InlineData(Win32Error.CallNotImplemented, (int)Win32ProcessIdentityReadStatus.Unavailable)]
    [InlineData(Win32Error.InvalidData, (int)Win32ProcessIdentityReadStatus.Ambiguous)]
    public void OpenFailureMapsWithoutGuessingIdentity(
        int error,
        int expectedRaw)
    {
        FakeIdentityNative native = FakeIdentityNative.Success(Created);
        native.OpenError = error;
        Win32ProcessIdentityReadStatus expected =
            (Win32ProcessIdentityReadStatus)expectedRaw;

        using Win32ProcessIdentityReadResult result = new Win32ProcessIdentityReader(native)
            .OpenAndRead(
                42,
                Win32ProcessAccess.QueryLimitedInformation | Win32ProcessAccess.Synchronize);

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Handle);
        Assert.Null(result.Identity);
    }

    [Fact]
    public void TokenAccessDeniedIsNotCollapsedIntoMalformedEvidence()
    {
        FakeIdentityNative native = FakeIdentityNative.Success(Created);
        native.TokenOpenSucceeded = false;
        native.TokenOpenError = Win32Error.AccessDenied;

        using Win32ProcessIdentityReadResult result = new Win32ProcessIdentityReader(native)
            .OpenAndRead(42, RequiredAccess);

        Assert.Equal(Win32ProcessIdentityReadStatus.AccessDenied, result.Status);
        Assert.Null(result.Handle);
        Assert.Null(result.Identity);
        Assert.True(native.ProcessHandle.IsClosed);
    }

    [Fact]
    public void ProcessThatSignalsDuringAQueryFailureIsAProvedExit()
    {
        FakeIdentityNative native = FakeIdentityNative.Success(Created);
        native.PathReplies.Enqueue(new(false, null, Win32Error.InvalidData));
        native.WaitResult = Win32ProcessWaitResult.Exited;

        using Win32ProcessIdentityReadResult result = new Win32ProcessIdentityReader(native)
            .OpenAndRead(42, RequiredAccess);

        Assert.Equal(Win32ProcessIdentityReadStatus.Exited, result.Status);
        Assert.Null(result.Handle);
        Assert.Null(result.Identity);
        Assert.True(native.ProcessHandle.IsClosed);
    }

    [Fact]
    public void ImageBufferGrowthStopsAtTheWin32Utf16Limit()
    {
        FakeIdentityNative native = FakeIdentityNative.Success(Created);
        for (int index = 0; index < 8; index++)
        {
            native.PathReplies.Enqueue(
                new(false, null, Win32Error.InsufficientBuffer));
        }

        using Win32ProcessIdentityReadResult result = new Win32ProcessIdentityReader(native)
            .OpenAndRead(42, RequiredAccess);

        Assert.Equal(Win32ProcessIdentityReadStatus.Ambiguous, result.Status);
        Assert.Equal(
            [260, 520, 1040, 2080, 4160, 8320, 16640, 32767],
            native.PathCapacities);
        Assert.True(native.ProcessHandle.IsClosed);
    }

    [Fact]
    public void ImageBufferFinalCapacityCanReturnTheLargestCanonicalPath()
    {
        FakeIdentityNative native = FakeIdentityNative.Success(Created);
        for (int index = 0; index < 7; index++)
        {
            native.PathReplies.Enqueue(
                new(false, null, Win32Error.InsufficientBuffer));
        }

        string path = DrivePathWithLength(
            Win32ExecutablePathCanonicalizer.MaximumCanonicalPathCharacters);
        native.PathReplies.Enqueue(new(true, path, Win32Error.Success));

        using Win32ProcessIdentityReadResult result = new Win32ProcessIdentityReader(native)
            .OpenAndRead(42, RequiredAccess);

        Assert.Equal(Win32ProcessIdentityReadStatus.Success, result.Status);
        Assert.Equal(path, result.Identity!.ExecutablePath, ignoreCase: true);
        Assert.Equal(
            Win32ExecutablePathCanonicalizer.MaximumQueryBufferCharacters,
            native.PathCapacities[^1]);
    }

    [Fact]
    public void ImageQueryClaimingToFillTheWholeBufferIsAmbiguous()
    {
        FakeIdentityNative native = FakeIdentityNative.Success(Created);
        native.PathReplies.Enqueue(new(
            true,
            DrivePathWithLength(260),
            Win32Error.Success));

        using Win32ProcessIdentityReadResult result = new Win32ProcessIdentityReader(native)
            .OpenAndRead(42, RequiredAccess);

        Assert.Equal(Win32ProcessIdentityReadStatus.Ambiguous, result.Status);
        Assert.True(native.ProcessHandle.IsClosed);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("nul")]
    [InlineData("high-surrogate")]
    [InlineData("low-surrogate")]
    public void EmptyNulOrIllFormedUtf16ImagePathIsAmbiguous(string fault)
    {
        string path = fault switch
        {
            "empty" => string.Empty,
            "nul" => "C:\\Games\\bad" + '\0' + ".exe",
            "high-surrogate" => "C:\\Games\\bad" + '\ud800' + ".exe",
            "low-surrogate" => "C:\\Games\\bad" + '\udc00' + ".exe",
            _ => throw new ArgumentOutOfRangeException(nameof(fault)),
        };
        FakeIdentityNative native = FakeIdentityNative.Success(Created);
        native.PathReplies.Enqueue(new(true, path, Win32Error.Success));

        using Win32ProcessIdentityReadResult result = new Win32ProcessIdentityReader(native)
            .OpenAndRead(42, RequiredAccess);

        Assert.Equal(Win32ProcessIdentityReadStatus.Ambiguous, result.Status);
        Assert.Null(result.Handle);
        Assert.Null(result.Identity);
    }

    [Fact]
    public void NativeSessionIdThatCannotFitTheManagedIdentityIsAmbiguous()
    {
        FakeIdentityNative native = FakeIdentityNative.Success(Created);
        native.SessionId = (uint)int.MaxValue + 1U;

        using Win32ProcessIdentityReadResult result = new Win32ProcessIdentityReader(native)
            .OpenAndRead(42, RequiredAccess);

        Assert.Equal(Win32ProcessIdentityReadStatus.Ambiguous, result.Status);
        Assert.Null(result.Identity);
    }

    [Fact]
    public void FailedPartialIdentityClosesBothOwningHandles()
    {
        FakeIdentityNative native = FakeIdentityNative.Success(Created);
        native.SidSucceeded = false;

        using Win32ProcessIdentityReadResult result = new Win32ProcessIdentityReader(native)
            .OpenAndRead(42, RequiredAccess);

        Assert.Equal(Win32ProcessIdentityReadStatus.Ambiguous, result.Status);
        Assert.True(native.ProcessHandle.IsClosed);
        Assert.True(native.TokenHandle.IsClosed);
    }

    [Fact]
    public void SuccessfulResultOwnsTheProcessHandleUntilItIsDisposed()
    {
        FakeIdentityNative native = FakeIdentityNative.Success(Created);
        Win32ProcessIdentityReadResult result = new Win32ProcessIdentityReader(native)
            .OpenAndRead(42, RequiredAccess);

        Assert.Equal(Win32ProcessIdentityReadStatus.Success, result.Status);
        Assert.False(native.ProcessHandle.IsClosed);
        Assert.True(native.TokenHandle.IsClosed);

        result.Dispose();

        Assert.True(native.ProcessHandle.IsClosed);
        result.Dispose();
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("creation")]
    [InlineData("path")]
    [InlineData("token-open")]
    [InlineData("sid")]
    [InlineData("session")]
    [InlineData("wait")]
    public void UnexpectedNativeExceptionFailsOpenAndClosesEveryAcquiredHandle(
        string stage)
    {
        FakeIdentityNative native = FakeIdentityNative.Success(Created);
        native.ThrowAt = stage;

        using Win32ProcessIdentityReadResult result = new Win32ProcessIdentityReader(native)
            .OpenAndRead(42, RequiredAccess);

        Assert.Equal(Win32ProcessIdentityReadStatus.Ambiguous, result.Status);
        Assert.True(native.ProcessHandle.IsClosed);
        if (stage is "sid" or "session" or "wait")
        {
            Assert.True(native.TokenHandle.IsClosed);
        }
    }

    [Theory]
    [InlineData("pid-mismatch")]
    [InlineData("creation-failure")]
    [InlineData("creation-underflow")]
    [InlineData("creation-overflow")]
    [InlineData("path-failure")]
    [InlineData("path-malformed")]
    [InlineData("token-open-failure")]
    [InlineData("sid-failure")]
    [InlineData("sid-malformed")]
    [InlineData("session-failure")]
    [InlineData("session-malformed")]
    [InlineData("wait-failed")]
    public void PartialOrMalformedIdentityIsAmbiguous(string fault)
    {
        FakeIdentityNative native = FakeIdentityNative.Success(Created);
        switch (fault)
        {
            case "pid-mismatch":
                native.ProcessId = 43;
                break;
            case "creation-failure":
                native.CreationSucceeded = false;
                break;
            case "creation-underflow":
                native.CreationFileTimeUtc = -1;
                break;
            case "creation-overflow":
                native.CreationFileTimeUtc = long.MaxValue;
                break;
            case "path-failure":
                native.PathReplies.Enqueue(new(false, null, Win32Error.InvalidData));
                break;
            case "path-malformed":
                native.PathReplies.Enqueue(new(true, @"relative.exe", Win32Error.Success));
                break;
            case "token-open-failure":
                native.TokenOpenSucceeded = false;
                native.TokenOpenError = Win32Error.InvalidData;
                break;
            case "sid-failure":
                native.SidSucceeded = false;
                break;
            case "sid-malformed":
                native.UserSid = "not-a-sid";
                break;
            case "session-failure":
                native.SessionSucceeded = false;
                break;
            case "session-malformed":
                native.SessionId = (uint)int.MaxValue + 1U;
                break;
            case "wait-failed":
                native.WaitResult = Win32ProcessWaitResult.Failed;
                break;
        }

        using Win32ProcessIdentityReadResult result = new Win32ProcessIdentityReader(native)
            .OpenAndRead(
                42,
                Win32ProcessAccess.QueryLimitedInformation | Win32ProcessAccess.Synchronize);

        Assert.Equal(Win32ProcessIdentityReadStatus.Ambiguous, result.Status);
        Assert.Null(result.Handle);
        Assert.Null(result.Identity);
    }

    [Fact]
    public void SignalledHandleIsAProvedExit()
    {
        FakeIdentityNative native = FakeIdentityNative.Success(Created);
        native.WaitResult = Win32ProcessWaitResult.Exited;

        using Win32ProcessIdentityReadResult result = new Win32ProcessIdentityReader(native)
            .OpenAndRead(
                42,
                Win32ProcessAccess.QueryLimitedInformation | Win32ProcessAccess.Synchronize);

        Assert.Equal(Win32ProcessIdentityReadStatus.Exited, result.Status);
        Assert.Null(result.Handle);
        Assert.Null(result.Identity);
    }

    private sealed class FakeIdentityNative : IWin32ProcessIdentityNative
    {
        private readonly SafeWin32ProcessHandle _process =
            new((nint)101, ownsHandle: false);
        private readonly SafeWin32TokenHandle _token =
            new((nint)201, ownsHandle: false);

        public int OpenError { get; set; } = Win32Error.Success;

        public int ProcessId { get; set; } = 42;

        public bool CreationSucceeded { get; set; } = true;

        public long CreationFileTimeUtc { get; set; }

        public bool TokenOpenSucceeded { get; set; } = true;

        public int TokenOpenError { get; set; } = Win32Error.AccessDenied;

        public string? ThrowAt { get; set; }

        public bool SidSucceeded { get; set; } = true;

        public string UserSid { get; set; } = "S-1-5-21-1000";

        public bool SessionSucceeded { get; set; } = true;

        public uint SessionId { get; set; } = 7;

        public Win32ProcessWaitResult WaitResult { get; set; } = Win32ProcessWaitResult.Alive;

        public List<Win32ProcessAccess> OpenAccesses { get; } = [];

        public List<SafeWin32ProcessHandle> ProcessHandleUses { get; } = [];

        public List<Win32TokenAccess> TokenAccesses { get; } = [];

        public List<int> PathCapacities { get; } = [];

        public Queue<Win32StringCallResult> PathReplies { get; } = [];

        public SafeWin32ProcessHandle ProcessHandle => _process;

        public SafeWin32TokenHandle TokenHandle => _token;

        public static FakeIdentityNative Success(DateTimeOffset created) =>
            new() { CreationFileTimeUtc = created.UtcDateTime.ToFileTimeUtc() };

        public SafeWin32ProcessHandle? OpenProcess(
            int pid,
            Win32ProcessAccess access,
            out int error)
        {
            OpenAccesses.Add(access);
            error = OpenError;
            return error == Win32Error.Success ? _process : null;
        }

        public bool TryGetProcessId(
            SafeWin32ProcessHandle process,
            out int pid,
            out int error)
        {
            ThrowIf("pid");
            ProcessHandleUses.Add(process);
            pid = ProcessId;
            error = Win32Error.Success;
            return true;
        }

        public bool TryGetCreationFileTime(
            SafeWin32ProcessHandle process,
            out long creationFileTimeUtc,
            out int error)
        {
            ThrowIf("creation");
            ProcessHandleUses.Add(process);
            creationFileTimeUtc = CreationFileTimeUtc;
            error = CreationSucceeded ? Win32Error.Success : Win32Error.InvalidData;
            return CreationSucceeded;
        }

        public Win32StringCallResult QueryFullProcessImageName(
            SafeWin32ProcessHandle process,
            int capacity)
        {
            ThrowIf("path");
            ProcessHandleUses.Add(process);
            PathCapacities.Add(capacity);
            return PathReplies.Count > 0
                ? PathReplies.Dequeue()
                : new(true, @"C:\Games\game.exe", Win32Error.Success);
        }

        public SafeWin32TokenHandle? OpenProcessToken(
            SafeWin32ProcessHandle process,
            Win32TokenAccess access,
            out int error)
        {
            ThrowIf("token-open");
            ProcessHandleUses.Add(process);
            TokenAccesses.Add(access);
            error = TokenOpenSucceeded ? Win32Error.Success : TokenOpenError;
            return TokenOpenSucceeded ? _token : null;
        }

        public bool TryGetTokenUserSid(
            SafeWin32TokenHandle token,
            out string sid,
            out int error)
        {
            ThrowIf("sid");
            sid = UserSid;
            error = SidSucceeded ? Win32Error.Success : Win32Error.InvalidData;
            return SidSucceeded;
        }

        public bool TryGetTokenSessionId(
            SafeWin32TokenHandle token,
            out uint sessionId,
            out int error)
        {
            ThrowIf("session");
            sessionId = SessionId;
            error = SessionSucceeded ? Win32Error.Success : Win32Error.InvalidData;
            return SessionSucceeded;
        }

        public Win32ProcessWaitResult WaitForProcess(
            SafeWin32ProcessHandle process,
            out int error)
        {
            ThrowIf("wait");
            ProcessHandleUses.Add(process);
            error = WaitResult == Win32ProcessWaitResult.Failed
                ? Win32Error.InvalidHandle
                : Win32Error.Success;
            return WaitResult;
        }

        private void ThrowIf(string stage)
        {
            if (string.Equals(ThrowAt, stage, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Injected native failure.");
            }
        }
    }

    private static string DrivePathWithLength(int length) =>
        @"C:\" + new string('a', length - 7) + ".exe";
}
