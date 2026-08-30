using System.Text;
using System.Text.Json;
using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class DesktopLegacyMigrationClientTests
{
    private const string Hash =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string RecoveryToken =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task List_DecodesPrivacySafeMigrationFacts()
    {
        RecordingTransport transport = new(Response(
            "listLegacyTaskMigrationsResult",
            "list-1",
            "{\"migrations\":["
                + MigrationJson("migration-a", "disabled", completed: true) + "]}"));
        NightGateDesktopClient client = new(transport, new RequestIds("list-1"));

        DesktopLegacyMigrationListResult result = await client
            .ListLegacyTaskMigrationsAsync();

        Assert.True(result.Available);
        DesktopLegacyTaskMigration migration = Assert.Single(result.Migrations);
        Assert.Equal(DesktopLegacyTaskMigrationStatus.Disabled, migration.Status);
        Assert.True(migration.DisabledStateVerified);
        Assert.Equal(@"\old shutdown", migration.TaskPath);
        using JsonDocument request = JsonDocument.Parse(transport.Requests.Single());
        Assert.Equal(
            "listLegacyTaskMigrations",
            request.RootElement.GetProperty("type").GetString());
        Assert.Empty(request.RootElement.GetProperty("payload").EnumerateObject());
    }

    [Fact]
    public async Task PrepareAndComplete_SendOnlyWhitelistedFacts()
    {
        RecordingTransport transport = new(
            Response(
                "prepareLegacyTaskMigrationResult",
                "prepare-1",
                "{\"accepted\":true,\"migration\":"
                    + MigrationJson("migration-a", "prepared") + "}"),
            Response(
                "completeLegacyTaskMigrationResult",
                "complete-1",
                "{\"accepted\":true,\"migration\":"
                    + MigrationJson("migration-a", "disabled", completed: true) + "}"));
        NightGateDesktopClient client = new(
            transport,
            new RequestIds("prepare-1", "complete-1"));
        LegacyShutdownTaskCandidate candidate = new(@"\old shutdown", Hash, true);

        DesktopLegacyMigrationMutationResult prepared = await client
            .PrepareLegacyTaskMigrationAsync(candidate);
        DesktopLegacyMigrationMutationResult completed = await client
            .CompleteLegacyTaskMigrationAsync(
                "migration-a",
                DesktopLegacyTaskMigrationStatus.Disabled);

        Assert.True(prepared.Accepted);
        Assert.True(completed.Accepted);
        using JsonDocument prepareRequest = JsonDocument.Parse(transport.Requests[0]);
        JsonElement preparePayload = prepareRequest.RootElement.GetProperty("payload");
        Assert.Equal(3, preparePayload.EnumerateObject().Count());
        Assert.Equal(Hash, preparePayload.GetProperty("actionFingerprint").GetString());
        Assert.False(preparePayload.TryGetProperty("taskXml", out _));
        using JsonDocument completeRequest = JsonDocument.Parse(transport.Requests[1]);
        Assert.Equal(
            "disabled",
            completeRequest.RootElement.GetProperty("payload")
                .GetProperty("status").GetString());
    }

    [Fact]
    public async Task Prepare_AcceptsCanonicalWindowsTaskPathWithDifferentCasing()
    {
        RecordingTransport transport = new(Response(
            "prepareLegacyTaskMigrationResult",
            "prepare-1",
            "{\"accepted\":true,\"migration\":"
                + MigrationJson(
                    "migration-a",
                    "prepared",
                    taskPath: @"\OLD SHUTDOWN") + "}"));
        NightGateDesktopClient client = new(transport, new RequestIds("prepare-1"));

        DesktopLegacyMigrationMutationResult result = await client
            .PrepareLegacyTaskMigrationAsync(new(@"\old shutdown", Hash, true));

        Assert.True(result.Accepted);
    }

    [Fact]
    public async Task CompleteRestorePreparation_UsesDurableProtocolStatus()
    {
        RecordingTransport transport = new(Response(
            "completeLegacyTaskMigrationResult",
            "restore-prepare-1",
            "{\"accepted\":true,\"migration\":"
                + MigrationJson("migration-a", "restorePrepared") + "}"));
        NightGateDesktopClient client = new(
            transport,
            new RequestIds("restore-prepare-1"));

        DesktopLegacyMigrationMutationResult result = await client
            .CompleteLegacyTaskMigrationAsync(
                "migration-a",
                DesktopLegacyTaskMigrationStatus.RestorePrepared);

        Assert.True(result.Accepted);
        Assert.Equal(
            DesktopLegacyTaskMigrationStatus.RestorePrepared,
            result.Migration!.Status);
        using JsonDocument request = JsonDocument.Parse(transport.Requests.Single());
        Assert.Equal(
            "restorePrepared",
            request.RootElement.GetProperty("payload")
                .GetProperty("status").GetString());
    }

    [Fact]
    public async Task FailedRecovery_UsesDedicatedLookupAndRecoveryMessages()
    {
        RecordingTransport transport = new(
            Response(
                "findLegacyTaskMigrationRecoveryCandidateResult",
                "lookup-1",
                "{\"found\":true,\"migration\":"
                    + MigrationJson("migration-a", "failed", completed: true)
                    + ",\"recoveryToken\":\"" + RecoveryToken + "\"}"),
            Response(
                "recoverLegacyTaskMigrationDisabledResult",
                "recover-1",
                "{\"accepted\":true,\"migration\":"
                    + MigrationJson("migration-a", "disabled", completed: true) + "}"));
        NightGateDesktopClient client = new(
            transport,
            new RequestIds("lookup-1", "recover-1"));

        DesktopLegacyMigrationLookupResult lookup = await client
            .FindLegacyTaskMigrationRecoveryCandidateAsync(@"\old shutdown");
        DesktopLegacyMigrationMutationResult recovered = await client
            .RecoverLegacyTaskMigrationDisabledAsync(
                lookup.Migration!,
                lookup.RecoveryToken!);

        Assert.True(lookup.Available);
        Assert.Equal(
            DesktopLegacyTaskMigrationStatus.Failed,
            lookup.Migration!.Status);
        Assert.True(recovered.Accepted);
        Assert.True(recovered.Migration!.DisabledStateVerified);
        using JsonDocument lookupRequest = JsonDocument.Parse(transport.Requests[0]);
        Assert.Equal(
            "findLegacyTaskMigrationRecoveryCandidate",
            lookupRequest.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            @"\old shutdown",
            lookupRequest.RootElement.GetProperty("payload")
                .GetProperty("taskPath").GetString());
        using JsonDocument recoverRequest = JsonDocument.Parse(transport.Requests[1]);
        Assert.Equal(
            "recoverLegacyTaskMigrationDisabled",
            recoverRequest.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            "migration-a",
            recoverRequest.RootElement.GetProperty("payload")
                .GetProperty("migrationId").GetString());
        JsonElement recoverPayload = recoverRequest.RootElement.GetProperty("payload");
        Assert.Equal(5, recoverPayload.EnumerateObject().Count());
        Assert.Equal(@"\old shutdown", recoverPayload.GetProperty("taskPath").GetString());
        Assert.Equal(Hash, recoverPayload.GetProperty("actionFingerprint").GetString());
        Assert.True(recoverPayload.GetProperty("originalEnabled").GetBoolean());
        Assert.Equal(RecoveryToken, recoverPayload.GetProperty("recoveryToken").GetString());
    }

    [Fact]
    public async Task List_FollowsBoundedPagesAndReturnsTerminalSummary()
    {
        RecordingTransport transport = new(
            Response(
                "listLegacyTaskMigrationsResult",
                "list-1",
                "{\"migrations\":["
                    + MigrationJson("migration-a", "disabled", completed: true)
                    + "],\"nextCursor\":\"migration-a\",\"failedCount\":7}"),
            Response(
                "listLegacyTaskMigrationsResult",
                "list-2",
                "{\"migrations\":["
                    + MigrationJson("migration-b", "prepared")
                    + "],\"nextCursor\":null,\"failedCount\":7}"));
        NightGateDesktopClient client = new(
            transport,
            new RequestIds("list-1", "list-2"));

        DesktopLegacyMigrationListResult result = await client
            .ListLegacyTaskMigrationsAsync();

        Assert.True(result.Available);
        Assert.Equal(2, result.Migrations.Count);
        Assert.Equal(7, result.FailedCount);
        Assert.Equal(2, transport.Requests.Count);
        using JsonDocument first = JsonDocument.Parse(transport.Requests[0]);
        Assert.Empty(first.RootElement.GetProperty("payload").EnumerateObject());
        using JsonDocument second = JsonDocument.Parse(transport.Requests[1]);
        Assert.Equal(
            "migration-a",
            second.RootElement.GetProperty("payload")
                .GetProperty("cursor").GetString());
    }

    private static string MigrationJson(
        string id,
        string status,
        bool completed = false,
        string taskPath = @"\old shutdown") => JsonSerializer.Serialize(new
        {
            migrationId = id,
            taskPath,
            actionFingerprint = Hash,
            originalEnabled = true,
            status,
            preparedAtUtc = "2026-07-15T14:00:00Z",
            completedAtUtc = completed ? "2026-07-15T14:01:00Z" : null,
            disabledStateVerified = status == "disabled",
        });

    private static string Response(string type, string requestId, string data) =>
        "{\"version\":1,\"type\":\"" + type + "\",\"requestId\":\""
        + requestId + "\",\"payload\":{\"status\":\"success\",\"data\":"
        + data + "}}";

    private sealed class RecordingTransport(params string[] responses) : INightGatePipeTransport
    {
        private readonly Queue<string> _responses = new(responses);

        public List<ReadOnlyMemory<byte>> Requests { get; } = [];

        public ValueTask<ReadOnlyMemory<byte>> ExchangeAsync(
            ReadOnlyMemory<byte> requestUtf8,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(requestUtf8.ToArray());
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(
                Encoding.UTF8.GetBytes(_responses.Dequeue()));
        }
    }

    private sealed class RequestIds(params string[] values) : IProtocolRequestIdSource
    {
        private readonly Queue<string> _values = new(values);

        public string NextRequestId() => _values.Dequeue();
    }
}
