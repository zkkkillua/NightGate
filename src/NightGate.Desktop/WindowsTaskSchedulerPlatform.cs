using System.Collections;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace NightGate.Desktop;

internal sealed class WindowsTaskSchedulerPlatform :
    ILegacyScheduledTaskPlatform
{
    private const int IncludeHiddenTasks = 1;
    private const int ExecuteActionType = 0;
    private const int ComHandlerActionType = 5;
    private const int SendEmailActionType = 6;
    private const int ShowMessageActionType = 7;
    private const int MaximumEmailHeaderCount = 32;
    private const int MaximumEmailAttachmentCount = 8;
    private const long MaximumDefinitionXmlCharacters = 4 * 1024 * 1024;
    private const int FileNotFoundHResult = unchecked((int)0x80070002);
    private const int PathNotFoundHResult = unchecked((int)0x80070003);
    private readonly Func<bool> _isWindows;
    private readonly Func<object?> _createConnectedService;
    private readonly Action<object?> _releaseComObject;

    public WindowsTaskSchedulerPlatform()
        : this(OperatingSystem.IsWindows, CreateConnectedService)
    {
    }

    internal WindowsTaskSchedulerPlatform(
        Func<bool> isWindows,
        Func<object?> createConnectedService)
        : this(isWindows, createConnectedService, ReleaseComObject)
    {
    }

    internal WindowsTaskSchedulerPlatform(
        Func<bool> isWindows,
        Func<object?> createConnectedService,
        Action<object?> releaseComObject)
    {
        ArgumentNullException.ThrowIfNull(isWindows);
        ArgumentNullException.ThrowIfNull(createConnectedService);
        ArgumentNullException.ThrowIfNull(releaseComObject);
        _isWindows = isWindows;
        _createConnectedService = createConnectedService;
        _releaseComObject = releaseComObject;
    }

    public LegacyScheduledTaskEnumerationResult Enumerate(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_isWindows())
        {
            return LegacyScheduledTaskEnumerationResult.Unavailable;
        }

        object? service = null;
        Stack<object> pendingFolders = new();
        try
        {
            service = _createConnectedService();
            if (service is null)
            {
                return LegacyScheduledTaskEnumerationResult.Unavailable;
            }

            object root = ((dynamic)service).GetFolder("\\");
            pendingFolders.Push(root);
            List<LegacyScheduledTaskSnapshot> tasks = [];
            bool complete = true;
            while (pendingFolders.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                object folder = pendingFolders.Pop();
                try
                {
                    try
                    {
                        complete &= ReadFolderTasks(
                            folder,
                            tasks,
                            cancellationToken);
                    }
                    catch (OperationCanceledException) when (
                        cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception) when (IsRecoverable(exception))
                    {
                        // An inaccessible task collection cannot hide child folders.
                        complete = false;
                    }

                    try
                    {
                        ReadChildFolders(folder, pendingFolders, cancellationToken);
                    }
                    catch (OperationCanceledException) when (
                        cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception) when (IsRecoverable(exception))
                    {
                        // Tasks already discovered remain usable.
                        complete = false;
                    }
                }
                finally
                {
                    _releaseComObject(folder);
                }
            }

            return new(complete, tasks.ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return LegacyScheduledTaskEnumerationResult.Unavailable;
        }
        finally
        {
            DrainComObjects(pendingFolders);
            _releaseComObject(service);
        }
    }

    public LegacyScheduledTaskReadResult Read(
        string taskPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_isWindows()
            || !TrySplitTaskPath(taskPath, out string folderPath, out string taskName))
        {
            return LegacyScheduledTaskReadResult.Unavailable;
        }

        object? service = null;
        object? folder = null;
        object? task = null;
        try
        {
            service = _createConnectedService();
            if (service is null)
            {
                return LegacyScheduledTaskReadResult.Unavailable;
            }

            folder = ((dynamic)service).GetFolder(folderPath);
            cancellationToken.ThrowIfCancellationRequested();
            task = ((dynamic)folder).GetTask(taskName);
            LegacyScheduledTaskSnapshot snapshot = ProjectTask(task);
            return LegacyScheduledTaskReadResult.Found(snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsMissing(exception))
        {
            return LegacyScheduledTaskReadResult.Missing;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return LegacyScheduledTaskReadResult.Unavailable;
        }
        finally
        {
            _releaseComObject(task);
            _releaseComObject(folder);
            _releaseComObject(service);
        }
    }

    public LegacyScheduledTaskSetEnabledStatus TrySetEnabled(
        LegacyScheduledTaskSnapshot expectedTask,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? taskPath = expectedTask?.TaskPath;
        if (!_isWindows()
            || expectedTask is null
            || !TrySplitTaskPath(taskPath, out string folderPath, out string taskName))
        {
            return LegacyScheduledTaskSetEnabledStatus.Unavailable;
        }

        object? service = null;
        object? folder = null;
        object? task = null;
        try
        {
            service = _createConnectedService();
            if (service is null)
            {
                return LegacyScheduledTaskSetEnabledStatus.Unavailable;
            }

            folder = ((dynamic)service).GetFolder(folderPath);
            cancellationToken.ThrowIfCancellationRequested();
            task = ((dynamic)folder).GetTask(taskName);
            LegacyScheduledTaskSnapshot currentTask = ProjectTask(task);
            if (!LegacyScheduledTaskSnapshotComparer.EqualsExact(
                    currentTask,
                    expectedTask))
            {
                return LegacyScheduledTaskSetEnabledStatus.Changed;
            }

            dynamic registeredTask = task;
            if (currentTask.Enabled == enabled)
            {
                return LegacyScheduledTaskSetEnabledStatus.Unchanged;
            }

            registeredTask.Enabled = enabled;
            cancellationToken.ThrowIfCancellationRequested();

            // Do not treat an accepted COM property assignment as proof that the
            // registered task changed. Re-open the task so the result reflects
            // Task Scheduler's persisted state rather than the current RCW.
            _releaseComObject(task);
            task = null;
            task = ((dynamic)folder).GetTask(taskName);
            LegacyScheduledTaskSnapshot verifiedTask = ProjectTask(task);
            LegacyScheduledTaskSnapshot desiredTask = expectedTask with
            {
                Enabled = enabled,
            };
            if (LegacyScheduledTaskSnapshotComparer.EqualsExact(
                    verifiedTask,
                    desiredTask))
            {
                return LegacyScheduledTaskSetEnabledStatus.Updated;
            }

            return LegacyScheduledTaskSnapshotComparer.EqualsExact(
                    verifiedTask,
                    currentTask)
                ? LegacyScheduledTaskSetEnabledStatus.Unavailable
                : LegacyScheduledTaskSetEnabledStatus.Changed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsMissing(exception))
        {
            return LegacyScheduledTaskSetEnabledStatus.Missing;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return LegacyScheduledTaskSetEnabledStatus.Unavailable;
        }
        finally
        {
            _releaseComObject(task);
            _releaseComObject(folder);
            _releaseComObject(service);
        }
    }

    private bool ReadFolderTasks(
        object folder,
        List<LegacyScheduledTaskSnapshot> destination,
        CancellationToken cancellationToken)
    {
        object? taskCollection = null;
        bool complete = true;
        try
        {
            taskCollection = ((dynamic)folder).GetTasks(IncludeHiddenTasks);
            foreach (object task in ObjectsIn(taskCollection))
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    destination.Add(ProjectTask(task));
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (IsRecoverable(exception))
                {
                    // Inaccessible or malformed tasks are not candidates.
                    complete = false;
                }
                finally
                {
                    _releaseComObject(task);
                }
            }

            return complete;
        }
        finally
        {
            _releaseComObject(taskCollection);
        }
    }

    private void ReadChildFolders(
        object folder,
        Stack<object> destination,
        CancellationToken cancellationToken)
    {
        object? folderCollection = null;
        try
        {
            folderCollection = ((dynamic)folder).GetFolders(0);
            foreach (object child in ObjectsIn(folderCollection))
            {
                bool transferred = false;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    destination.Push(child);
                    transferred = true;
                }
                finally
                {
                    if (!transferred)
                    {
                        _releaseComObject(child);
                    }
                }
            }
        }
        finally
        {
            _releaseComObject(folderCollection);
        }
    }

    private LegacyScheduledTaskSnapshot ProjectTask(object task)
    {
        dynamic registeredTask = task;
        string taskPath = Convert.ToString(
                registeredTask.Path,
                CultureInfo.InvariantCulture)
            ?? string.Empty;
        bool enabled = Convert.ToBoolean(
            registeredTask.Enabled,
            CultureInfo.InvariantCulture);
        DateTimeOffset? lastRunTimeUtc = TryReadLastRunTimeUtc(registeredTask);
        int? lastTaskResult = TryReadLastTaskResult(registeredTask);
        string definitionFingerprint = FingerprintDefinition(
            ConvertActionString(registeredTask.Xml));
        object? definition = null;
        object? actionCollection = null;
        try
        {
            definition = registeredTask.Definition;
            actionCollection = ((dynamic)definition).Actions;
            List<LegacyScheduledTaskActionSnapshot> actions = [];
            foreach (object action in ObjectsIn(actionCollection))
            {
                try
                {
                    actions.Add(ProjectAction(action));
                }
                finally
                {
                    _releaseComObject(action);
                }
            }

            return new(
                taskPath,
                enabled,
                actions.ToArray(),
                definitionFingerprint,
                lastRunTimeUtc,
                lastTaskResult);
        }
        finally
        {
            _releaseComObject(actionCollection);
            _releaseComObject(definition);
        }
    }

    private LegacyScheduledTaskActionSnapshot ProjectAction(object action)
    {
        dynamic scheduledAction = action;
        int type = Convert.ToInt32(
            scheduledAction.Type,
            CultureInfo.InvariantCulture);
        string? actionId = ConvertActionString(scheduledAction.Id);
        return type switch
        {
            ExecuteActionType => new(
                LegacyScheduledTaskActionKind.Execute,
                ConvertActionString(scheduledAction.Path),
                ConvertActionString(scheduledAction.Arguments),
                ConvertActionString(scheduledAction.WorkingDirectory),
                type,
                actionId),
            ComHandlerActionType => new(
                LegacyScheduledTaskActionKind.Other,
                null,
                null,
                null,
                type,
                actionId,
                [
                    new("ClassId", ConvertActionString(scheduledAction.ClassId)),
                    new("Data", ConvertActionString(scheduledAction.Data)),
                ]),
            SendEmailActionType => new(
                LegacyScheduledTaskActionKind.Other,
                null,
                null,
                null,
                type,
                actionId,
                ProjectEmailProperties(action)),
            ShowMessageActionType => new(
                LegacyScheduledTaskActionKind.Other,
                null,
                null,
                null,
                type,
                actionId,
                [
                    new("Title", ConvertActionString(scheduledAction.Title)),
                    new(
                        "MessageBody",
                        ConvertActionString(scheduledAction.MessageBody)),
                ]),
            _ => throw new InvalidDataException(
                "Task Scheduler returned an unsupported action type."),
        };
    }

    private IReadOnlyList<LegacyScheduledTaskActionPropertySnapshot>
        ProjectEmailProperties(object action)
    {
        dynamic emailAction = action;
        List<LegacyScheduledTaskActionPropertySnapshot> properties =
        [
            new("Server", ConvertActionString(emailAction.Server)),
            new("Subject", ConvertActionString(emailAction.Subject)),
            new("To", ConvertActionString(emailAction.To)),
            new("Cc", ConvertActionString(emailAction.Cc)),
            new("Bcc", ConvertActionString(emailAction.Bcc)),
            new("ReplyTo", ConvertActionString(emailAction.ReplyTo)),
            new("From", ConvertActionString(emailAction.From)),
        ];
        object? headerFields = null;
        object? attachments = null;
        try
        {
            headerFields = emailAction.HeaderFields;
            AppendNamedValueProperties(
                properties,
                "HeaderFields",
                headerFields,
                MaximumEmailHeaderCount);
            properties.Add(new(
                "Body",
                ConvertActionString(emailAction.Body)));
            attachments = emailAction.Attachments;
            AppendStringCollectionProperties(
                properties,
                "Attachments",
                attachments,
                MaximumEmailAttachmentCount);
            return properties.ToArray();
        }
        finally
        {
            _releaseComObject(attachments);
            _releaseComObject(headerFields);
        }
    }

    private void AppendNamedValueProperties(
        List<LegacyScheduledTaskActionPropertySnapshot> destination,
        string prefix,
        object? collection,
        int maximumCount)
    {
        destination.Add(new(
            $"{prefix}.State",
            collection is null ? "null" : "present"));
        if (collection is null)
        {
            return;
        }

        int count = 0;
        foreach (object pair in ObjectsIn(collection))
        {
            try
            {
                if (++count > maximumCount)
                {
                    throw new InvalidDataException(
                        "Task Scheduler returned too many named values.");
                }

                dynamic namedValue = pair;
                destination.Add(new(
                    $"{prefix}.Name",
                    ConvertActionString(namedValue.Name)));
                destination.Add(new(
                    $"{prefix}.Value",
                    ConvertActionString(namedValue.Value)));
            }
            finally
            {
                _releaseComObject(pair);
            }
        }
    }

    private static void AppendStringCollectionProperties(
        List<LegacyScheduledTaskActionPropertySnapshot> destination,
        string prefix,
        object? collection,
        int maximumCount)
    {
        destination.Add(new(
            $"{prefix}.State",
            collection is null ? "null" : "present"));
        if (collection is null)
        {
            return;
        }

        if (collection is string)
        {
            throw new InvalidDataException(
                "Task Scheduler returned a scalar where an array was required.");
        }

        int count = 0;
        foreach (object value in ObjectsIn(collection))
        {
            if (++count > maximumCount)
            {
                throw new InvalidDataException(
                    "Task Scheduler returned too many string values.");
            }

            destination.Add(new(
                $"{prefix}.Item",
                ConvertActionString(value)));
        }
    }

    private static string? ConvertActionString(object? value) =>
        value is null
            ? null
            : Convert.ToString(value, CultureInfo.InvariantCulture);

    private static DateTimeOffset? TryReadLastRunTimeUtc(dynamic registeredTask)
    {
        try
        {
            object? value = registeredTask.LastRunTime;
            if (value is null)
            {
                return null;
            }

            DateTime timestamp = Convert.ToDateTime(value, CultureInfo.InvariantCulture);
            // Task Scheduler uses an early sentinel when a task has never run.
            if (timestamp.Year < 1900)
            {
                return null;
            }

            DateTime utc = timestamp.Kind switch
            {
                DateTimeKind.Utc => timestamp,
                DateTimeKind.Local => timestamp.ToUniversalTime(),
                _ when TimeZoneInfo.Local.IsInvalidTime(timestamp) => default,
                _ => TimeZoneInfo.ConvertTimeToUtc(
                    DateTime.SpecifyKind(timestamp, DateTimeKind.Unspecified),
                    TimeZoneInfo.Local),
            };
            return utc == default
                ? null
                : new DateTimeOffset(utc, TimeSpan.Zero);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Runtime telemetry is optional and must never hide a readable task.
            return null;
        }
    }

    private static int? TryReadLastTaskResult(dynamic registeredTask)
    {
        try
        {
            return Convert.ToInt32(
                registeredTask.LastTaskResult,
                CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // A task that never ran or denies telemetry remains safe to inspect.
            return null;
        }
    }

    private static string FingerprintDefinition(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)
            || xml.Length > MaximumDefinitionXmlCharacters)
        {
            throw new InvalidDataException(
                "Task Scheduler returned an invalid task definition.");
        }

        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = false,
            IgnoreWhitespace = true,
            MaxCharactersInDocument = MaximumDefinitionXmlCharacters,
            XmlResolver = null,
        };
        using StringReader input = new(xml);
        using XmlReader reader = XmlReader.Create(input, settings);
        XDocument document = XDocument.Load(reader, LoadOptions.None);
        XElement root = document.Root
            ?? throw new InvalidDataException(
                "Task Scheduler returned an empty task definition.");
        if (!string.Equals(root.Name.LocalName, "Task", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Task Scheduler returned an unexpected task definition.");
        }

        foreach (XElement enabled in root.Elements()
                     .Where(element => string.Equals(
                         element.Name.LocalName,
                         "Settings",
                         StringComparison.Ordinal))
                     .SelectMany(settingsElement => settingsElement.Elements())
                     .Where(element => string.Equals(
                         element.Name.LocalName,
                         "Enabled",
                         StringComparison.Ordinal))
                     .ToArray())
        {
            enabled.Remove();
        }

        string canonical = root.ToString(SaveOptions.DisableFormatting);
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static IEnumerable<object> ObjectsIn(object collection)
    {
        if (collection is not IEnumerable enumerable)
        {
            throw new InvalidDataException("Task Scheduler collection is unavailable.");
        }

        foreach (object? value in enumerable)
        {
            if (value is null)
            {
                throw new InvalidDataException("Task Scheduler returned a missing item.");
            }

            yield return value;
        }
    }

    private static bool TrySplitTaskPath(
        string? taskPath,
        out string folderPath,
        out string taskName)
    {
        folderPath = string.Empty;
        taskName = string.Empty;
        if (string.IsNullOrWhiteSpace(taskPath)
            || taskPath.Length > 1_024
            || !taskPath.StartsWith("\\", StringComparison.Ordinal)
            || taskPath.EndsWith("\\", StringComparison.Ordinal)
            || taskPath.Contains('/')
            || taskPath.Contains('\0'))
        {
            return false;
        }

        int separator = taskPath.LastIndexOf('\\');
        if (separator < 0 || separator == taskPath.Length - 1)
        {
            return false;
        }

        folderPath = separator == 0 ? "\\" : taskPath[..separator];
        taskName = taskPath[(separator + 1)..];
        return taskName.Length > 0;
    }

    private static object? CreateConnectedService()
    {
        Type? serviceType = Type.GetTypeFromProgID(
            "Schedule.Service",
            throwOnError: false);
        if (serviceType is null)
        {
            return null;
        }

        object? service = null;
        try
        {
            service = Activator.CreateInstance(serviceType);
            if (service is null)
            {
                return null;
            }

            ((dynamic)service).Connect();
            return service;
        }
        catch
        {
            ReleaseComObject(service);
            throw;
        }
    }

    private static bool IsMissing(Exception exception) =>
        exception is FileNotFoundException or DirectoryNotFoundException
        || exception is COMException
        {
            HResult: FileNotFoundHResult or PathNotFoundHResult,
        };

    private static bool IsRecoverable(Exception exception) =>
        exception is not OutOfMemoryException
        and not StackOverflowException
        and not AccessViolationException;

    private void DrainComObjects(Stack<object> objects)
    {
        while (objects.TryPop(out object? value))
        {
            _releaseComObject(value);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is null || !Marshal.IsComObject(value))
        {
            return;
        }

        try
        {
            _ = Marshal.ReleaseComObject(value);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
        }
    }
}
