using System.Text.RegularExpressions;
using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class LegacyShutdownTaskPlatformContractTests
{
    [Fact]
    public void PlatformSeam_ExposesOnlyReadAndSingleEnabledMutation()
    {
        Assert.Equal(
            ["Enumerate", "Read", "TrySetEnabled"],
            typeof(ILegacyScheduledTaskPlatform)
                .GetMethods()
                .Select(method => method.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.NotNull(typeof(LegacyShutdownTaskAdapter).GetConstructor([]));
    }

    [Fact]
    public void ProductionSource_CannotDeleteLaunchCreateOrEditScheduledTasks()
    {
        string root = FindRepositoryRoot();
        string adapter = File.ReadAllText(Path.Combine(
            root,
            "src",
            "NightGate.Desktop",
            "LegacyShutdownTaskAdapter.cs"));
        string platform = File.ReadAllText(Path.Combine(
            root,
            "src",
            "NightGate.Desktop",
            "WindowsTaskSchedulerPlatform.cs"));
        string source = adapter + "\n" + platform;
        string[] forbidden =
        [
            "DeleteTask",
            "schtasks",
            "System.Diagnostics.Process",
            "Process.Start",
            "ProcessStartInfo",
            "CreateProcess",
            "ShellExecute",
            "WinExec",
            "WScript.Shell",
            "System.Management.Automation",
            "RegisterTask",
            "RegisterTaskDefinition",
            "NewTask",
            "CreateTask",
            "Actions.Create",
            "Triggers.Create",
            "XmlText",
            "TaskDefinition.Xml",
            "IRegisteredTask.Xml",
        ];

        foreach (string token in forbidden)
        {
            Assert.DoesNotContain(token, source, StringComparison.OrdinalIgnoreCase);
        }

        // Wrapper executable names may be inspected as scheduled-task data, but
        // the adapter still cannot launch them; the process APIs above remain
        // forbidden.

        Assert.DoesNotMatch(
            new Regex(@"\.(?:Path|Arguments|Definition)\s*=", RegexOptions.CultureInvariant),
            platform);
        Assert.DoesNotMatch(
            new Regex(@"\.Triggers\b", RegexOptions.CultureInvariant),
            platform);
    }

    [Fact]
    public void NonWindowsPlatform_IsUnavailableWithoutOpeningCom()
    {
        int factoryCalls = 0;
        WindowsTaskSchedulerPlatform platform = new(
            isWindows: () => false,
            createConnectedService: () =>
            {
                factoryCalls++;
                throw new InvalidOperationException("must not open COM");
            });

        LegacyScheduledTaskEnumerationResult enumeration = platform.Enumerate();
        Assert.False(enumeration.Complete);
        Assert.Empty(enumeration.Tasks);
        Assert.Equal(
            LegacyScheduledTaskReadStatus.Unavailable,
            platform.Read(@"\old shutdown").Status);
        Assert.Equal(
            LegacyScheduledTaskSetEnabledStatus.Unavailable,
            platform.TrySetEnabled(
                Snapshot(@"\old shutdown", enabled: true),
                false));
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public void Platform_EnumeratesFoldersAndProjectsCompleteActionShapeUsingFakeCom()
    {
        FakeFolder child = new(
            @"\child",
            [
                new FakeTask(
                    @"\child\multiple",
                    enabled: false,
                    new FakeAction(
                        0,
                        @"C:\Windows\System32\shutdown.exe",
                        "/s",
                        @"C:\Windows\System32"),
                    new FakeAction(5, null, null)),
            ]);
        FakeFolder root = new(
            @"\",
            [
                new FakeTask(
                    @"\single",
                    enabled: true,
                    new FakeAction(0, @"%SystemRoot%\System32\shutdown.exe", "/s /t 0")),
                new FaultingFakeTask(),
            ],
            [child]);
        WindowsTaskSchedulerPlatform platform = FakeWindows(root);

        LegacyScheduledTaskEnumerationResult enumeration = platform.Enumerate();
        LegacyScheduledTaskSnapshot[] tasks = enumeration.Tasks.ToArray();

        Assert.False(enumeration.Complete);
        Assert.Equal([@"\single", @"\child\multiple"], tasks.Select(task => task.TaskPath));
        Assert.True(tasks[0].Enabled);
        Assert.Equal(
            new LegacyScheduledTaskActionSnapshot(
                LegacyScheduledTaskActionKind.Execute,
                @"%SystemRoot%\System32\shutdown.exe",
                "/s /t 0",
                null,
                0),
            Assert.Single(tasks[0].Actions));
        Assert.False(tasks[1].Enabled);
        Assert.Equal(@"C:\Windows\System32", tasks[1].Actions[0].WorkingDirectory);
        Assert.Equal(5, tasks[1].Actions[1].NativeType);
        Assert.Equal(
            [LegacyScheduledTaskActionKind.Execute, LegacyScheduledTaskActionKind.Other],
            tasks[1].Actions.Select(action => action.Kind));
    }

    [Fact]
    public void Platform_InaccessibleTaskCollectionStillTraversesChildFolders()
    {
        FakeFolder child = new(
            @"\child",
            [
                new FakeTask(
                    @"\child\candidate",
                    enabled: true,
                    new FakeAction(0, @"C:\Windows\System32\shutdown.exe", "/s")),
            ]);
        FakeFolder root = new(
            @"\",
            [],
            [child],
            getTasksException: new IOException("root task collection inaccessible"));
        WindowsTaskSchedulerPlatform platform = FakeWindows(root);

        LegacyScheduledTaskEnumerationResult enumeration = platform.Enumerate();
        LegacyScheduledTaskSnapshot task = Assert.Single(enumeration.Tasks);

        Assert.False(enumeration.Complete);
        Assert.Equal(@"\child\candidate", task.TaskPath);
    }

    [Fact]
    public void Platform_FullyReadableTreeReportsCompleteEnumeration()
    {
        FakeFolder root = new(
            @"\",
            [
                new FakeTask(
                    @"\candidate",
                    enabled: true,
                    new FakeAction(0, @"C:\Windows\System32\shutdown.exe", "/s")),
            ]);
        WindowsTaskSchedulerPlatform platform = FakeWindows(root);

        LegacyScheduledTaskEnumerationResult enumeration = platform.Enumerate();

        Assert.True(enumeration.Complete);
        Assert.Single(enumeration.Tasks);
    }

    [Fact]
    public void Platform_ChangesOnlyEnabledWithoutChangingDefinitionFingerprint()
    {
        FakeTask task = new(
            @"\folder\old shutdown",
            enabled: true,
            new FakeAction(0, @"C:\Windows\System32\shutdown.exe", "/s"));
        FakeFolder child = new(@"\folder", [task]);
        FakeFolder root = new(@"\", [], [child]);
        WindowsTaskSchedulerPlatform platform = FakeWindows(root);

        LegacyScheduledTaskReadResult read = platform.Read(task.Path);
        LegacyScheduledTaskSetEnabledStatus changed =
            platform.TrySetEnabled(read.Task!, false);
        LegacyScheduledTaskReadResult disabled = platform.Read(task.Path);
        LegacyScheduledTaskSetEnabledStatus unchanged =
            platform.TrySetEnabled(disabled.Task!, false);

        Assert.Equal(LegacyScheduledTaskReadStatus.Found, read.Status);
        Assert.Equal(task.Path, read.Task!.TaskPath);
        Assert.Equal(LegacyScheduledTaskSetEnabledStatus.Updated, changed);
        Assert.Equal(LegacyScheduledTaskSetEnabledStatus.Unchanged, unchanged);
        Assert.Equal(
            read.Task.DefinitionFingerprint,
            disabled.Task!.DefinitionFingerprint);
        Assert.False(task.Enabled);
        Assert.Equal(1, task.EnabledSetCount);
    }

    [Fact]
    public void Platform_ReadProjectsLastRunTelemetryWithoutChangingTask()
    {
        DateTime lastRunUtc = new(
            2026,
            7,
            18,
            16,
            10,
            1,
            DateTimeKind.Utc);
        FakeTask task = new(
            @"\old shutdown",
            enabled: false,
            new FakeAction(0, @"C:\Windows\System32\shutdown.exe", "/s"))
        {
            LastRunTime = lastRunUtc,
            LastTaskResult = 0,
        };
        WindowsTaskSchedulerPlatform platform = FakeWindows(
            new FakeFolder(@"\", [task]));

        LegacyScheduledTaskSnapshot snapshot = platform.Read(task.Path).Task!;

        Assert.Equal(new DateTimeOffset(lastRunUtc), snapshot.LastRunTimeUtc);
        Assert.Equal(0, snapshot.LastTaskResult);
        Assert.Equal(0, task.EnabledSetCount);
    }

    [Fact]
    public void Platform_DefinitionFingerprintCoversTriggerPrincipalSettingsAndRegistration()
    {
        FakeAction action = new(
            0,
            @"C:\Windows\System32\shutdown.exe",
            "/s");
        FakeTask[] tasks =
        [
            new(@"\base", true, action),
            new(@"\trigger", true, action)
            {
                XmlTemplate = DefinitionXml(trigger: "2026-07-20T00:11:00"),
            },
            new(@"\principal", true, action)
            {
                XmlTemplate = DefinitionXml(userId: "S-1-5-21-2000"),
            },
            new(@"\settings", true, action)
            {
                XmlTemplate = DefinitionXml(multipleInstances: "StopExisting"),
            },
            new(@"\conditions", true, action)
            {
                XmlTemplate = DefinitionXml(stopOnIdleEnd: false),
            },
            new(@"\registration", true, action)
            {
                XmlTemplate = DefinitionXml(author: "different-author"),
            },
            new(@"\xml-action", true, action)
            {
                XmlTemplate = DefinitionXml(actionArguments: "/p"),
            },
        ];
        WindowsTaskSchedulerPlatform platform = FakeWindows(
            new FakeFolder(@"\", tasks));

        LegacyScheduledTaskSnapshot[] projected = platform
            .Enumerate()
            .Tasks
            .ToArray();

        Assert.Equal(7, projected.Length);
        Assert.All(
            projected,
            task => Assert.Matches(
                "^[0-9a-f]{64}$",
                task.DefinitionFingerprint));
        Assert.Equal(
            7,
            projected
                .Select(task => task.DefinitionFingerprint)
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Fact]
    public void Platform_DoesNotReportUpdatedUntilTheEnabledWriteReadsBackDisabled()
    {
        FakeTask task = new(
            @"\old shutdown",
            enabled: true,
            new FakeAction(0, @"C:\Windows\System32\shutdown.exe", "/s /t 60"))
        {
            IgnoreEnabledWrites = true,
        };
        WindowsTaskSchedulerPlatform platform = FakeWindows(
            new FakeFolder(@"\", [task]));
        LegacyScheduledTaskReadResult before = platform.Read(task.Path);

        LegacyScheduledTaskSetEnabledStatus result = platform.TrySetEnabled(
            before.Task!,
            enabled: false);

        Assert.NotEqual(LegacyScheduledTaskSetEnabledStatus.Updated, result);
        Assert.True(task.Enabled);
        Assert.Equal(1, task.EnabledSetCount);
    }

    [Fact]
    public void Platform_ComHandlerIdentityAndPayloadChangeCandidateFingerprint()
    {
        FakeAction baseline = new(
            5,
            Id: "handler-action",
            ClassId: "{11111111-1111-1111-1111-111111111111}",
            Data: "payload-a");

        LegacyShutdownTaskCandidate[] candidates = ScanCandidates(
            baseline,
            baseline with
            {
                Id = "handler-action-b",
            },
            baseline with
            {
                ClassId = "{22222222-2222-2222-2222-222222222222}",
            },
            baseline with
            {
                Data = "payload-b",
            });

        Assert.Equal(
            candidates.Length,
            candidates.Select(item => item.ActionFingerprint)
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Fact]
    public void Platform_EveryEmailPropertyChangesCandidateFingerprint()
    {
        FakeAction baseline = new(
            6,
            Id: "email-action",
            Server: "smtp.example.test",
            Subject: "subject-a",
            To: "to@example.test",
            Cc: "cc@example.test",
            Bcc: "bcc@example.test",
            ReplyTo: "reply@example.test",
            From: "from@example.test",
            HeaderFields: [new("X-NightGate", "a")],
            Body: "body-a",
            Attachments: [@"C:\A.txt"]);

        LegacyShutdownTaskCandidate[] candidates = ScanCandidates(
            baseline,
            baseline with { Server = "smtp-b.example.test" },
            baseline with { Subject = "subject-b" },
            baseline with { To = "to-b@example.test" },
            baseline with { Cc = "cc-b@example.test" },
            baseline with { Bcc = "bcc-b@example.test" },
            baseline with { ReplyTo = "reply-b@example.test" },
            baseline with { From = "from-b@example.test" },
            baseline with { HeaderFields = [new("X-NightGate", "b")] },
            baseline with { HeaderFields = [] },
            baseline with { HeaderFields = null },
            baseline with { Body = "body-b" },
            baseline with { Attachments = [@"C:\B.txt"] },
            baseline with { Attachments = [] },
            baseline with { Attachments = null });

        Assert.Equal(
            candidates.Length,
            candidates.Select(item => item.ActionFingerprint)
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Fact]
    public void Platform_ShowMessageTitleAndBodyChangeCandidateFingerprint()
    {
        FakeAction baseline = new(
            7,
            Id: "message-action",
            Title: "title-a",
            MessageBody: "message-a");

        LegacyShutdownTaskCandidate[] candidates = ScanCandidates(
            baseline,
            baseline with { Title = "title-b" },
            baseline with { MessageBody = "message-b" });

        Assert.Equal(
            candidates.Length,
            candidates.Select(item => item.ActionFingerprint)
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Fact]
    public void Platform_FaultingKnownActionPropertyCannotDisableTask()
    {
        (object Action, int Type)[] faultingActions =
        [
            (new FaultingActionId(), 5),
            (new FaultingComHandlerAction(), 5),
            (new FaultingEmailAction(), 6),
            (new FaultingShowMessageAction(), 7),
            (new FakeAction(99, Id: "future-action"), 99),
        ];

        foreach ((object action, int type) in faultingActions)
        {
            FakeTask task = new(
                $@"\old shutdown-{type}-{action.GetType().Name}",
                enabled: true,
                new FakeAction(
                    0,
                    @"C:\Windows\System32\shutdown.exe",
                    "/s",
                    Id: "shutdown-action"),
                action);
            WindowsTaskSchedulerPlatform platform = FakeWindows(
                new FakeFolder(@"\", [task]));
            LegacyScheduledTaskSnapshot expected = new(
                task.Path,
                true,
                [
                    new LegacyScheduledTaskActionSnapshot(
                        LegacyScheduledTaskActionKind.Execute,
                        @"C:\Windows\System32\shutdown.exe",
                        "/s",
                        NativeType: 0),
                    new LegacyScheduledTaskActionSnapshot(
                        LegacyScheduledTaskActionKind.Other,
                        null,
                        null,
                        NativeType: type),
                ],
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

            LegacyScheduledTaskEnumerationResult enumeration = platform.Enumerate();
            LegacyScheduledTaskReadResult read = platform.Read(task.Path);
            LegacyScheduledTaskSetEnabledStatus mutation = platform.TrySetEnabled(
                expected,
                enabled: false);

            Assert.False(enumeration.Complete);
            Assert.Empty(enumeration.Tasks);
            Assert.Equal(LegacyScheduledTaskReadStatus.Unavailable, read.Status);
            Assert.Equal(LegacyScheduledTaskSetEnabledStatus.Unavailable, mutation);
            Assert.True(task.Enabled);
            Assert.Equal(0, task.EnabledSetCount);
        }
    }

    [Fact]
    public void Platform_ChangedNonExecutePayloadCannotInheritPriorRead()
    {
        MutableComHandlerAction handler = new()
        {
            Data = "payload-a",
        };
        FakeTask task = new(
            @"\old shutdown",
            enabled: true,
            new FakeAction(
                0,
                @"C:\Windows\System32\shutdown.exe",
                "/s",
                Id: "shutdown-action"),
            handler);
        WindowsTaskSchedulerPlatform platform = FakeWindows(
            new FakeFolder(@"\", [task]));
        LegacyScheduledTaskReadResult before = platform.Read(task.Path);

        handler.Data = "payload-b";
        LegacyScheduledTaskSetEnabledStatus result = platform.TrySetEnabled(
            before.Task!,
            enabled: false);

        Assert.Equal(LegacyScheduledTaskSetEnabledStatus.Changed, result);
        Assert.True(task.Enabled);
        Assert.Equal(0, task.EnabledSetCount);
    }

    [Fact]
    public void Platform_MissingOrInaccessibleTaskReturnsNonMutatingStatus()
    {
        FakeFolder root = new(@"\", []);
        WindowsTaskSchedulerPlatform platform = FakeWindows(root);

        Assert.Equal(
            LegacyScheduledTaskReadStatus.Missing,
            platform.Read(@"\missing").Status);
        Assert.Equal(
            LegacyScheduledTaskSetEnabledStatus.Missing,
            platform.TrySetEnabled(Snapshot(@"\missing", enabled: true), false));
        Assert.Equal(
            LegacyScheduledTaskReadStatus.Unavailable,
            platform.Read("not-an-absolute-task-path").Status);
    }

    [Fact]
    public void Platform_CancellationReleasesCurrentTaskAndOwningComObjects()
    {
        using CancellationTokenSource cancellation = new();
        FakeTask task = new(
            @"\old shutdown",
            enabled: true,
            new FakeAction(0, @"C:\Windows\System32\shutdown.exe", "/s"));
        CancelBeforeFirstList<object> tasks = new(cancellation, task);
        FakeFolder root = new(@"\", tasks);
        FakeScheduleService service = new(root);
        List<object> released = [];
        WindowsTaskSchedulerPlatform platform = new(
            () => true,
            () => service,
            value =>
            {
                if (value is not null)
                {
                    released.Add(value);
                }
            });

        Assert.Throws<OperationCanceledException>(() =>
            platform.Enumerate(cancellation.Token));

        Assert.Equal(1, released.Count(item => ReferenceEquals(item, task)));
        Assert.Equal(1, released.Count(item => ReferenceEquals(item, tasks)));
        Assert.Equal(1, released.Count(item => ReferenceEquals(item, root)));
        Assert.Equal(1, released.Count(item => ReferenceEquals(item, service)));
    }

    [Fact]
    public void Platform_CancellationReleasesCurrentAndPendingFolderObjects()
    {
        using CancellationTokenSource cancellation = new();
        FakeFolder first = new(@"\first", []);
        FakeFolder second = new(@"\second", []);
        CancelOnSecondList<FakeFolder> folders = new(
            cancellation,
            first,
            second);
        FakeFolder root = new(@"\", [], folders);
        FakeScheduleService service = new(root);
        List<object> released = [];
        WindowsTaskSchedulerPlatform platform = new(
            () => true,
            () => service,
            value =>
            {
                if (value is not null)
                {
                    released.Add(value);
                }
            });

        Assert.Throws<OperationCanceledException>(() =>
            platform.Enumerate(cancellation.Token));

        Assert.Equal(1, released.Count(item => ReferenceEquals(item, first)));
        Assert.Equal(1, released.Count(item => ReferenceEquals(item, second)));
        Assert.Equal(1, released.Count(item => ReferenceEquals(item, folders)));
        Assert.Equal(1, released.Count(item => ReferenceEquals(item, root)));
        Assert.Equal(1, released.Count(item => ReferenceEquals(item, service)));
    }

    private static WindowsTaskSchedulerPlatform FakeWindows(FakeFolder root)
    {
        FakeScheduleService service = new(root);
        return new(() => true, () => service);
    }

    private static LegacyShutdownTaskCandidate[] ScanCandidates(
        params FakeAction[] secondaryActions)
    {
        FakeTask[] tasks = secondaryActions
            .Select((action, index) => new FakeTask(
                $@"\candidate-{index}",
                enabled: true,
                new FakeAction(
                    0,
                    @"C:\Windows\System32\shutdown.exe",
                    "/s /t 0",
                    Id: "shutdown-action"),
                action))
            .ToArray();
        LegacyShutdownTaskAdapter adapter = new(
            FakeWindows(new FakeFolder(@"\", tasks)),
            value => value.Replace(
                "%SystemRoot%",
                @"C:\Windows",
                StringComparison.OrdinalIgnoreCase));

        return adapter.Scan().ToArray();
    }

    private static LegacyScheduledTaskSnapshot Snapshot(
        string path,
        bool enabled) => new(
        path,
        enabled,
        [
            new LegacyScheduledTaskActionSnapshot(
                LegacyScheduledTaskActionKind.Execute,
                @"C:\Windows\System32\shutdown.exe",
                "/s"),
        ],
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

    private static string DefinitionXml(
        string trigger = "2026-07-20T00:10:00",
        string userId = "S-1-5-21-1001",
        string multipleInstances = "IgnoreNew",
        string author = "NightGate tests",
        bool stopOnIdleEnd = true,
        string actionArguments = "/s") => $$"""
        <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task" version="1.4">
          <RegistrationInfo><Author>{{author}}</Author></RegistrationInfo>
          <Triggers><TimeTrigger><StartBoundary>{{trigger}}</StartBoundary><Enabled>true</Enabled></TimeTrigger></Triggers>
          <Principals><Principal id="Author"><UserId>{{userId}}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
          <Settings><MultipleInstancesPolicy>{{multipleInstances}}</MultipleInstancesPolicy><Enabled>{ENABLED}</Enabled><IdleSettings><StopOnIdleEnd>{{stopOnIdleEnd.ToString().ToLowerInvariant()}}</StopOnIdleEnd></IdleSettings></Settings>
          <Actions Context="Author"><Exec><Command>C:\Windows\System32\shutdown.exe</Command><Arguments>{{actionArguments}}</Arguments></Exec></Actions>
        </Task>
        """;

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null
            && !File.Exists(Path.Combine(current.FullName, "NightGate.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate NightGate.slnx.");
    }

    public sealed class FakeScheduleService(FakeFolder root)
    {
        public object GetFolder(string path) => root.FindFolder(path)
            ?? throw new FileNotFoundException("folder missing");
    }

    public sealed class FakeFolder(
        string path,
        IReadOnlyList<object> tasks,
        IReadOnlyList<FakeFolder>? folders = null,
        Exception? getTasksException = null)
    {
        private readonly IReadOnlyList<FakeFolder> _folders = folders ?? [];

        public string Path { get; } = path;

        public IReadOnlyList<object> GetTasks(int flags)
        {
            if (getTasksException is not null)
            {
                throw getTasksException;
            }

            return tasks;
        }

        public IReadOnlyList<FakeFolder> GetFolders(int flags) => _folders;

        public object GetTask(string name) => tasks
            .OfType<FakeTask>()
            .SingleOrDefault(task => string.Equals(
                task.Name,
                name,
                StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("task missing");

        public FakeFolder? FindFolder(string expectedPath)
        {
            if (string.Equals(Path, expectedPath, StringComparison.OrdinalIgnoreCase))
            {
                return this;
            }

            foreach (FakeFolder child in _folders)
            {
                FakeFolder? found = child.FindFolder(expectedPath);
                if (found is not null)
                {
                    return found;
                }
            }

            return null;
        }
    }

    public class FakeTask
    {
        private bool _enabled;

        public FakeTask(string path, bool enabled, params object[] actions)
        {
            Path = path;
            Name = path[(path.LastIndexOf('\\') + 1)..];
            _enabled = enabled;
            Definition = new(actions);
        }

        public virtual string Path { get; }

        public string Name { get; }

        public bool Enabled
        {
            get => _enabled;
            set
            {
                EnabledSetCount++;
                if (!IgnoreEnabledWrites)
                {
                    _enabled = value;
                }
            }
        }

        public int EnabledSetCount { get; private set; }

        public bool IgnoreEnabledWrites { get; init; }

        public string XmlTemplate { get; init; } = DefinitionXml();

        public DateTime LastRunTime { get; init; }

        public int LastTaskResult { get; init; }

        public string Xml => XmlTemplate.Replace(
            "{ENABLED}",
            Enabled ? "true" : "false",
            StringComparison.Ordinal);

        public FakeDefinition Definition { get; }
    }

    public sealed class FaultingFakeTask : FakeTask
    {
        public FaultingFakeTask()
            : base(@"\fault", true, new FakeAction(0, "ignored", "/s"))
        {
        }

        public override string Path => throw new IOException("task inaccessible");
    }

    public sealed class FakeDefinition(IReadOnlyList<object> actions)
    {
        public IReadOnlyList<object> Actions { get; } = actions;
    }

    public sealed record FakeAction(
        int Type,
        string? Path = null,
        string? Arguments = null,
        string? WorkingDirectory = null,
        string? Id = null,
        string? ClassId = null,
        string? Data = null,
        string? Server = null,
        string? Subject = null,
        string? To = null,
        string? Cc = null,
        string? Bcc = null,
        string? ReplyTo = null,
        string? From = null,
        IReadOnlyList<FakeNamedValue>? HeaderFields = null,
        string? Body = null,
        IReadOnlyList<string>? Attachments = null,
        string? Title = null,
        string? MessageBody = null);

    public sealed record FakeNamedValue(string Name, string Value);

    public sealed class FaultingComHandlerAction
    {
        public int Type => 5;

        public string Id => "handler-action";

        public string ClassId => throw new IOException("COM property unavailable");

        public string Data => "payload";
    }

    public sealed class FaultingActionId
    {
        public int Type => 5;

        public string Id => throw new IOException("COM property unavailable");

        public string ClassId => "{11111111-1111-1111-1111-111111111111}";

        public string Data => "payload";
    }

    public sealed class FaultingEmailAction
    {
        public int Type => 6;

        public string Id => "email-action";

        public string Server => "smtp.example.test";

        public string Subject => "subject";

        public string To => "to@example.test";

        public string Cc => string.Empty;

        public string Bcc => string.Empty;

        public string ReplyTo => string.Empty;

        public string From => "from@example.test";

        public object HeaderFields => throw new IOException("COM property unavailable");

        public string Body => "body";

        public IReadOnlyList<string> Attachments => [];
    }

    public sealed class FaultingShowMessageAction
    {
        public int Type => 7;

        public string Id => "message-action";

        public string Title => throw new IOException("COM property unavailable");

        public string MessageBody => "message";
    }

    public sealed class MutableComHandlerAction
    {
        public int Type => 5;

        public string Id => "handler-action";

        public string ClassId => "{11111111-1111-1111-1111-111111111111}";

        public string Data { get; set; } = string.Empty;
    }

    private sealed class CancelBeforeFirstList<T>(
        CancellationTokenSource cancellation,
        T item) : IReadOnlyList<T>
    {
        public int Count => 1;

        public T this[int index] => index == 0
            ? item
            : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<T> GetEnumerator()
        {
            cancellation.Cancel();
            yield return item;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private sealed class CancelOnSecondList<T>(
        CancellationTokenSource cancellation,
        T first,
        T second) : IReadOnlyList<T>
    {
        public int Count => 2;

        public T this[int index] => index switch
        {
            0 => first,
            1 => second,
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };

        public IEnumerator<T> GetEnumerator()
        {
            yield return first;
            cancellation.Cancel();
            yield return second;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }
}
