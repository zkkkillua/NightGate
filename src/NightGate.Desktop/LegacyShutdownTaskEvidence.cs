using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NightGate.Desktop;

internal sealed record LegacyShutdownTaskEvidenceEntry(
    string MigrationId,
    string TaskPath,
    string ActionFingerprint,
    string MigrationStatus,
    string IdentityStatus,
    bool? Enabled,
    DateTimeOffset? LastRunTimeLocal,
    DateTimeOffset? LastRunTimeUtc,
    int? LastTaskResult);

internal sealed record LegacyShutdownTaskEvidenceDocument(
    int SchemaVersion,
    DateOnly ProbeDateLocal,
    DateTimeOffset CheckedAtLocal,
    DateTimeOffset CheckedAtUtc,
    string Status,
    string? Error,
    IReadOnlyList<LegacyShutdownTaskEvidenceEntry> Tasks);

internal interface ILegacyShutdownTaskEvidenceCapture
{
    ValueTask<LegacyShutdownTaskEvidenceDocument> CaptureAsync(
        DateTimeOffset checkedAtLocal,
        CancellationToken cancellationToken = default);
}

internal interface ILegacyShutdownTaskEvidenceSink
{
    ValueTask<DateOnly?> ReadLatestProbeDateAsync(
        CancellationToken cancellationToken = default);

    ValueTask WriteAsync(
        LegacyShutdownTaskEvidenceDocument document,
        CancellationToken cancellationToken = default);
}

internal sealed class LegacyShutdownTaskEvidenceCaptureService :
    ILegacyShutdownTaskEvidenceCapture
{
    private const int MaximumTasks = 1_024;
    private readonly IDesktopLegacyMigrationService _migrationService;
    private readonly ILegacyShutdownTaskEvidenceReader _reader;
    private readonly TimeZoneInfo _localTimeZone;

    internal LegacyShutdownTaskEvidenceCaptureService(
        IDesktopLegacyMigrationService migrationService,
        ILegacyShutdownTaskEvidenceReader reader,
        TimeZoneInfo localTimeZone)
    {
        ArgumentNullException.ThrowIfNull(migrationService);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(localTimeZone);
        _migrationService = migrationService;
        _reader = reader;
        _localTimeZone = localTimeZone;
    }

    public async ValueTask<LegacyShutdownTaskEvidenceDocument> CaptureAsync(
        DateTimeOffset checkedAtLocal,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset local = TimeZoneInfo.ConvertTime(
            checkedAtLocal,
            _localTimeZone);
        DesktopLegacyMigrationListResult listed;
        try
        {
            listed = await _migrationService.ListAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return Inconclusive(local, "service-unavailable");
        }

        if (!listed.Available)
        {
            return Inconclusive(local, ErrorToken(listed.Error, "service-unavailable"));
        }

        DesktopLegacyTaskMigration[] migrations = listed.Migrations
            .Where(migration => migration is not null
                && migration.Status is DesktopLegacyTaskMigrationStatus.Prepared
                    or DesktopLegacyTaskMigrationStatus.Disabled
                    or DesktopLegacyTaskMigrationStatus.RestorePrepared)
            .OrderBy(migration => migration.MigrationId, StringComparer.Ordinal)
            .Take(MaximumTasks + 1)
            .ToArray();
        if (migrations.Length == 0)
        {
            return Inconclusive(local, "no-managed-tasks");
        }

        if (migrations.Length > MaximumTasks)
        {
            return Inconclusive(local, "too-many-managed-tasks");
        }

        LegacyShutdownTaskCandidate[] candidates = migrations
            .Select(CandidateFor)
            .ToArray();
        IReadOnlyList<LegacyShutdownTaskRuntimeEvidence> observed;
        try
        {
            observed = _reader.ReadRuntimeEvidence(candidates, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return Inconclusive(local, "scheduler-read-unavailable");
        }

        Dictionary<string, LegacyShutdownTaskRuntimeEvidence> observations = new(
            StringComparer.OrdinalIgnoreCase);
        foreach (LegacyShutdownTaskRuntimeEvidence? evidence in observed)
        {
            if (evidence is not null
                && evidence.TaskPath.Length > 0
                && !observations.ContainsKey(evidence.TaskPath))
            {
                observations.Add(evidence.TaskPath, evidence);
            }
        }

        List<LegacyShutdownTaskEvidenceEntry> tasks = [];
        bool complete = true;
        foreach (DesktopLegacyTaskMigration migration in migrations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            observations.TryGetValue(
                migration.TaskPath,
                out LegacyShutdownTaskRuntimeEvidence? evidence);
            LegacyTaskObservationStatus identityStatus = evidence?.Status
                ?? LegacyTaskObservationStatus.Unavailable;
            complete &= identityStatus is LegacyTaskObservationStatus.MatchingDisabled
                or LegacyTaskObservationStatus.MatchingEnabled;
            DateTimeOffset? lastRunUtc = evidence?.LastRunTimeUtc?.ToUniversalTime();
            DateTimeOffset? lastRunLocal = lastRunUtc is { } lastRun
                ? TimeZoneInfo.ConvertTime(lastRun, _localTimeZone)
                : null;
            tasks.Add(new(
                migration.MigrationId,
                migration.TaskPath,
                migration.ActionFingerprint,
                MigrationStatusToken(migration.Status),
                IdentityStatusToken(identityStatus),
                evidence?.Enabled,
                lastRunLocal,
                lastRunUtc,
                evidence?.LastTaskResult));
        }

        return new(
            SchemaVersion: 1,
            ProbeDateLocal: DateOnly.FromDateTime(local.DateTime),
            CheckedAtLocal: local,
            CheckedAtUtc: local.ToUniversalTime(),
            Status: complete ? "complete" : "inconclusive",
            Error: complete ? null : "task-read-inconclusive",
            Tasks: tasks.ToArray());
    }

    private static LegacyShutdownTaskEvidenceDocument Inconclusive(
        DateTimeOffset local,
        string error) => new(
        SchemaVersion: 1,
        ProbeDateLocal: DateOnly.FromDateTime(local.DateTime),
        CheckedAtLocal: local,
        CheckedAtUtc: local.ToUniversalTime(),
        Status: "inconclusive",
        Error: error,
        Tasks: []);

    private static LegacyShutdownTaskCandidate CandidateFor(
        DesktopLegacyTaskMigration migration) => new(
        migration.TaskPath,
        migration.ActionFingerprint,
        migration.OriginalEnabled);

    private static string MigrationStatusToken(
        DesktopLegacyTaskMigrationStatus status) => status switch
        {
            DesktopLegacyTaskMigrationStatus.Prepared => "prepared",
            DesktopLegacyTaskMigrationStatus.Disabled => "disabled",
            DesktopLegacyTaskMigrationStatus.RestorePrepared => "restorePrepared",
            DesktopLegacyTaskMigrationStatus.Restored => "restored",
            DesktopLegacyTaskMigrationStatus.Failed => "failed",
            _ => "unknown",
        };

    private static string IdentityStatusToken(
        LegacyTaskObservationStatus status) => status switch
        {
            LegacyTaskObservationStatus.MatchingEnabled => "matchingEnabled",
            LegacyTaskObservationStatus.MatchingDisabled => "matchingDisabled",
            LegacyTaskObservationStatus.Changed => "changed",
            LegacyTaskObservationStatus.Missing => "missing",
            LegacyTaskObservationStatus.Unavailable => "unavailable",
            LegacyTaskObservationStatus.Invalid => "invalid",
            _ => "invalid",
        };

    private static string ErrorToken(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 64
            || !value.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_'))
        {
            return fallback;
        }

        return value;
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not OutOfMemoryException
        and not StackOverflowException
        and not AccessViolationException;
}

internal sealed class LocalLegacyShutdownTaskEvidenceSink :
    ILegacyShutdownTaskEvidenceSink
{
    internal const string FileName = "legacy-shutdown-task-evidence.json";
    private const long MaximumEvidenceBytes = 256 * 1024;
    private readonly string _path;
    private readonly JsonSerializerOptions _serializerOptions = new(
        JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal LocalLegacyShutdownTaskEvidenceSink()
        : this(DefaultPath())
    {
    }

    internal static ILegacyShutdownTaskEvidenceSink CreateForCurrentUser(
        Func<string>? localDataRoot = null)
    {
        try
        {
            return new LocalLegacyShutdownTaskEvidenceSink(
                DefaultPath(localDataRoot));
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return UnavailableLegacyShutdownTaskEvidenceSink.Instance;
        }
    }

    internal LocalLegacyShutdownTaskEvidenceSink(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException(
                "The evidence path must be absolute.",
                nameof(path));
        }

        _path = Path.GetFullPath(path);
    }

    internal string PathForDiagnostics => _path;

    public async ValueTask<DateOnly?> ReadLatestProbeDateAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileInfo file = new(_path);
        if (!file.Exists || file.Length is <= 0 or > MaximumEvidenceBytes)
        {
            return null;
        }

        try
        {
            await using FileStream stream = new(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            LegacyShutdownTaskEvidenceDocument? document = await JsonSerializer
                .DeserializeAsync<LegacyShutdownTaskEvidenceDocument>(
                    stream,
                    _serializerOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            return LegacyShutdownTaskEvidenceContract.IsValid(document)
                ? document!.ProbeDateLocal
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return null;
        }
    }

    public async ValueTask WriteAsync(
        LegacyShutdownTaskEvidenceDocument document,
        CancellationToken cancellationToken = default)
    {
        if (!LegacyShutdownTaskEvidenceContract.IsValid(document))
        {
            throw new InvalidDataException("The scheduled-task evidence is invalid.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        string directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidDataException("The evidence directory is unavailable.");
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(
            directory,
            $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream stream = new(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        document,
                        _serializerOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                if (stream.Length > MaximumEvidenceBytes)
                {
                    throw new InvalidDataException(
                        "The scheduled-task evidence exceeds its size limit.");
                }
            }

            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
            }
        }
    }

    private static string DefaultPath(Func<string>? localDataRoot = null)
    {
        string root = localDataRoot?.Invoke()
            ?? Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
        {
            throw new InvalidOperationException(
                "The current user's local application data directory is unavailable.");
        }

        return Path.Combine(root, "NightGate", "Diagnostics", FileName);
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not OutOfMemoryException
        and not StackOverflowException
        and not AccessViolationException;
}

internal sealed class UnavailableLegacyShutdownTaskEvidenceSink :
    ILegacyShutdownTaskEvidenceSink
{
    internal static UnavailableLegacyShutdownTaskEvidenceSink Instance { get; } =
        new();

    private UnavailableLegacyShutdownTaskEvidenceSink()
    {
    }

    public ValueTask<DateOnly?> ReadLatestProbeDateAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<DateOnly?>(null);
    }

    public ValueTask WriteAsync(
        LegacyShutdownTaskEvidenceDocument document,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromException(
            new IOException("The diagnostic evidence directory is unavailable."));
    }
}

internal static class LegacyShutdownTaskEvidenceContract
{
    private const int MaximumTasks = 1_024;

    internal static bool IsValid(LegacyShutdownTaskEvidenceDocument? document)
    {
        if (document is null
            || document.SchemaVersion != 1
            || document.ProbeDateLocal == default
            || document.CheckedAtLocal == default
            || document.CheckedAtUtc == default
            || document.CheckedAtUtc.Offset != TimeSpan.Zero
            || document.CheckedAtLocal.ToUniversalTime() != document.CheckedAtUtc
            || DateOnly.FromDateTime(document.CheckedAtLocal.DateTime)
                != document.ProbeDateLocal
            || document.Status is not ("complete" or "inconclusive")
            || document.Status == "complete" && document.Error is not null
            || document.Status == "inconclusive"
                && !IsToken(document.Error, maximumLength: 64)
            || document.Tasks is null
            || document.Tasks.Count > MaximumTasks)
        {
            return false;
        }

        foreach (LegacyShutdownTaskEvidenceEntry? task in document.Tasks)
        {
            if (task is null
                || !IsText(task.MigrationId, 128)
                || !IsText(task.TaskPath, 1_024)
                || !IsFingerprint(task.ActionFingerprint)
                || !IsToken(task.MigrationStatus, 32)
                || !IsToken(task.IdentityStatus, 32)
                || task.LastRunTimeUtc is { Offset: var offset }
                    && offset != TimeSpan.Zero
                || task.LastRunTimeUtc is null != (task.LastRunTimeLocal is null)
                || task.LastRunTimeUtc is { } utc
                    && task.LastRunTimeLocal is { } local
                    && utc != local.ToUniversalTime())
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && string.Equals(value, value.Trim(), StringComparison.Ordinal)
        && !value.Contains('\0');

    private static bool IsToken(string? value, int maximumLength) =>
        IsText(value, maximumLength)
        && value!.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '-' or '_');

    private static bool IsFingerprint(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9'
            or >= 'a' and <= 'f');
}

internal sealed class LegacyShutdownTaskEvidenceRuntime : IAsyncDisposable
{
    private static readonly TimeOnly ProbeTime = new(0, 11);
    private static readonly TimeSpan MaximumClockRecheck = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MinimumClockRecheck = TimeSpan.FromSeconds(1);
    private readonly ILegacyShutdownTaskEvidenceCapture _capture;
    private readonly ILegacyShutdownTaskEvidenceSink _sink;
    private readonly IDesktopRuntimeClock _clock;
    private readonly TimeZoneInfo _localTimeZone;
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _lifecycle = new();
    private Task? _startTask;
    private Task? _loop;
    private Task? _stopTask;
    private bool _latestDateLoaded;
    private DateOnly? _latestPersistedDate;
    private DateOnly? _completedDate;
    private DateTimeOffset? _retryNotBeforeLocal;

    internal LegacyShutdownTaskEvidenceRuntime(
        ILegacyShutdownTaskEvidenceCapture capture,
        ILegacyShutdownTaskEvidenceSink sink,
        IDesktopRuntimeClock clock,
        TimeZoneInfo localTimeZone)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(localTimeZone);
        _capture = capture;
        _sink = sink;
        _clock = clock;
        _localTimeZone = localTimeZone;
    }

    internal static LegacyShutdownTaskEvidenceRuntime CreateForCurrentUser(
        IDesktopLegacyMigrationService migrationService,
        ILegacyShutdownTaskEvidenceReader reader,
        IDesktopRuntimeClock clock,
        Func<TimeZoneInfo>? localTimeZone = null,
        Func<ILegacyShutdownTaskEvidenceSink>? sink = null)
    {
        ArgumentNullException.ThrowIfNull(migrationService);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(clock);
        try
        {
            TimeZoneInfo timeZone = localTimeZone?.Invoke() ?? TimeZoneInfo.Local;
            ILegacyShutdownTaskEvidenceSink evidenceSink = sink?.Invoke()
                ?? LocalLegacyShutdownTaskEvidenceSink.CreateForCurrentUser();
            return new(
                new LegacyShutdownTaskEvidenceCaptureService(
                    migrationService,
                    reader,
                    timeZone),
                evidenceSink,
                clock,
                timeZone);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return new(
                UnavailableLegacyShutdownTaskEvidenceCapture.Instance,
                UnavailableLegacyShutdownTaskEvidenceSink.Instance,
                clock,
                TimeZoneInfo.Utc);
        }
    }

    public Task StartAsync()
    {
        lock (_lifecycle)
        {
            if (_stopTask is not null)
            {
                throw new ObjectDisposedException(
                    nameof(LegacyShutdownTaskEvidenceRuntime));
            }

            return _startTask ??= StartCoreAsync(_stopping.Token);
        }
    }

    public Task StopAsync()
    {
        lock (_lifecycle)
        {
            return _stopTask ??= StopCoreAsync();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    internal async ValueTask<bool> CaptureIfDueAsync(
        CancellationToken cancellationToken = default)
    {
        await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTimeOffset localNow = TimeZoneInfo.ConvertTime(
                _clock.Now,
                _localTimeZone);
            DateOnly localDate = DateOnly.FromDateTime(localNow.DateTime);
            if (TimeOnly.FromDateTime(localNow.DateTime) < ProbeTime)
            {
                return false;
            }

            if (!_latestDateLoaded)
            {
                try
                {
                    _latestPersistedDate = await _sink
                        .ReadLatestProbeDateAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (IsRecoverable(exception))
                {
                    _latestPersistedDate = null;
                }

                _latestDateLoaded = true;
            }

            if (_latestPersistedDate is { } persisted && persisted >= localDate
                || _completedDate is { } completed && completed >= localDate
                || _retryNotBeforeLocal is { } retryNotBefore
                    && retryNotBefore > localNow)
            {
                return false;
            }

            LegacyShutdownTaskEvidenceDocument document;
            try
            {
                document = await _capture
                    .CaptureAsync(localNow, cancellationToken)
                    .ConfigureAwait(false);
                if (document.ProbeDateLocal != localDate)
                {
                    throw new InvalidDataException(
                        "The evidence probe date does not match the scheduler date.");
                }

            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                document = new(
                    SchemaVersion: 1,
                    ProbeDateLocal: localDate,
                    CheckedAtLocal: localNow,
                    CheckedAtUtc: localNow.ToUniversalTime(),
                    Status: "inconclusive",
                    Error: "capture-failed",
                    Tasks: []);
            }

            try
            {
                await _sink.WriteAsync(document, cancellationToken).ConfigureAwait(false);
                _latestPersistedDate = localDate;
                _completedDate = localDate;
                _retryNotBeforeLocal = null;
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                // Retry on the next bounded clock cycle, never in a hot loop.
                _retryNotBeforeLocal = localNow + MaximumClockRecheck;
            }

            return true;
        }
        finally
        {
            _captureGate.Release();
        }
    }

    private Task StartCoreAsync(CancellationToken cancellationToken)
    {
        _loop = RunLoopAsync(cancellationToken);
        return Task.CompletedTask;
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                _ = await CaptureIfDueAsync(cancellationToken).ConfigureAwait(false);
                TimeSpan delay = DelayUntilClockRecheck();
                await _clock.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // No diagnostic failure may terminate or delay the enforcement runtimes.
        }
    }

    private TimeSpan DelayUntilClockRecheck()
    {
        DateTimeOffset localNow = TimeZoneInfo.ConvertTime(
            _clock.Now,
            _localTimeZone);
        DateTime localTargetDate = localNow.TimeOfDay < ProbeTime.ToTimeSpan()
            ? localNow.Date
            : localNow.Date.AddDays(1);
        DateTimeOffset target = new(
            localTargetDate + ProbeTime.ToTimeSpan(),
            _localTimeZone.GetUtcOffset(localTargetDate + ProbeTime.ToTimeSpan()));
        TimeSpan delay = target - localNow;
        if (delay <= TimeSpan.Zero)
        {
            return MinimumClockRecheck;
        }

        return delay < MaximumClockRecheck ? delay : MaximumClockRecheck;
    }

    private async Task StopCoreAsync()
    {
        _stopping.Cancel();
        Task? loop = _loop;
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stopping.Dispose();
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not OutOfMemoryException
        and not StackOverflowException
        and not AccessViolationException;
}

internal sealed class UnavailableLegacyShutdownTaskEvidenceCapture :
    ILegacyShutdownTaskEvidenceCapture
{
    internal static UnavailableLegacyShutdownTaskEvidenceCapture Instance { get; } =
        new();

    private UnavailableLegacyShutdownTaskEvidenceCapture()
    {
    }

    public ValueTask<LegacyShutdownTaskEvidenceDocument> CaptureAsync(
        DateTimeOffset checkedAtLocal,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new LegacyShutdownTaskEvidenceDocument(
            SchemaVersion: 1,
            ProbeDateLocal: DateOnly.FromDateTime(checkedAtLocal.DateTime),
            CheckedAtLocal: checkedAtLocal,
            CheckedAtUtc: checkedAtLocal.ToUniversalTime(),
            Status: "inconclusive",
            Error: "capture-unavailable",
            Tasks: []));
    }
}
