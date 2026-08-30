using System.Collections.Immutable;
using System.Text.Json;
using NightGate.Core;
using NightGate.Protocol;

namespace NightGate.Desktop.Tests;

public sealed class PipeProcessPersistenceStoreTests
{
    [Fact]
    public async Task GateLoad_FoundRoundTripsObjectPayloadWithinFrame()
    {
        ProcessGateEnvelope envelope = SimpleEnvelope(1);
        Assert.True(ProcessPersistenceJsonCodec.TrySerializeEnvelope(envelope, out string payload));
        ScriptedTransport transport = new(request => ResponseForRequest(
            request,
            "loadProcessPersistenceResult",
            "found",
            ProcessPersistenceSlot.ProcessGateEnvelope,
            envelope.Revision,
            payload));
        PipeProcessGateEnvelopeStore store = new(transport, new FixedRequestIds());

        ProcessGateEnvelopeLoadResult result = await store.LoadAsync();

        Assert.Equal(ProcessGateStoreLoadStatus.Found, result.Status);
        Assert.Equal(1, result.Envelope!.Revision);
        Assert.Single(transport.Requests);
        Assert.True(transport.Requests[0].Length <= NightGateProtocol.MaximumBodyBytes);
        using JsonDocument request = JsonDocument.Parse(transport.Requests[0]);
        Assert.Equal(
            "processGateEnvelope",
            request.RootElement.GetProperty("payload").GetProperty("slot").GetString());
    }

    [Fact]
    public async Task GateLoad_MissingMapsToNotFound()
    {
        ScriptedTransport transport = new(request => ResponseForRequest(
            request,
            "loadProcessPersistenceResult",
            "missing",
            record: null));
        PipeProcessGateEnvelopeStore store = new(transport, new FixedRequestIds());

        ProcessGateEnvelopeLoadResult result = await store.LoadAsync();

        Assert.Equal(ProcessGateStoreLoadStatus.NotFound, result.Status);
        Assert.Null(result.Envelope);
    }

    [Fact]
    public async Task GateCompareExchange_UsesNullForInitialExpectedVersionAndEmbedsPayloadObject()
    {
        ProcessGateEnvelope replacement = SimpleEnvelope(1);
        Assert.True(ProcessPersistenceJsonCodec.TrySerializeEnvelope(replacement, out string payload));
        ScriptedTransport transport = new(request => ResponseForRequest(
            request,
            "compareExchangeProcessPersistenceResult",
            "saved",
            ProcessPersistenceSlot.ProcessGateEnvelope,
            replacement.Revision,
            payload));
        PipeProcessGateEnvelopeStore store = new(transport, new FixedRequestIds());

        ProcessGateEnvelopeSaveResult result = await store.CompareExchangeAsync(0, replacement);

        Assert.Equal(ProcessGateStoreSaveStatus.Saved, result.Status);
        using JsonDocument request = JsonDocument.Parse(transport.Requests.Single());
        JsonElement command = request.RootElement.GetProperty("payload");
        Assert.Equal(JsonValueKind.Null, command.GetProperty("expectedVersion").ValueKind);
        Assert.Equal(JsonValueKind.Object, command.GetProperty("payload").ValueKind);
    }

    [Fact]
    public async Task GateCompareExchange_TamperedSavedEchoReturnsCorrupt()
    {
        ProcessGateEnvelope replacement = SimpleEnvelope(1);
        ProcessGateEnvelope tampered = replacement with { NextJournalSequence = 2 };
        Assert.True(ProcessPersistenceJsonCodec.TrySerializeEnvelope(tampered, out string payload));
        ScriptedTransport transport = new(request => ResponseForRequest(
            request,
            "compareExchangeProcessPersistenceResult",
            "saved",
            ProcessPersistenceSlot.ProcessGateEnvelope,
            tampered.Revision,
            payload));
        PipeProcessGateEnvelopeStore store = new(transport, new FixedRequestIds());

        ProcessGateEnvelopeSaveResult result = await store.CompareExchangeAsync(0, replacement);

        Assert.Equal(ProcessGateStoreSaveStatus.Corrupt, result.Status);
        Assert.Null(result.Envelope);
    }

    [Fact]
    public async Task ContinuityCompareExchange_ConflictReturnsCurrentWinner()
    {
        ProcessSourceContinuityCheckpoint winner = new(
            1,
            ProcessSourceContinuityPhase.FreshLost,
            "winner-epoch",
            0,
            null);
        Assert.True(ProcessPersistenceJsonCodec.TrySerializeContinuity(winner, out string payload));
        ScriptedTransport transport = new(request => ResponseForRequest(
            request,
            "compareExchangeProcessPersistenceResult",
            "conflict",
            ProcessPersistenceSlot.ProcessSourceContinuity,
            winner.Version,
            payload));
        PipeProcessSourceContinuityStore store = new(transport, new FixedRequestIds());
        ProcessSourceContinuityCheckpoint proposed = winner with
        {
            Version = 3,
            ObserverEpoch = "proposed-epoch",
        };

        ProcessSourceContinuityStoreSaveResult result = await store.CompareExchangeAsync(
            expectedVersion: 2,
            proposed);

        Assert.Equal(ProcessSourceContinuityStoreSaveStatus.Conflict, result.Status);
        Assert.Equal(winner, result.Checkpoint);
    }

    [Fact]
    public async Task TransportOrMalformedResponse_FailsOpenAsUnavailable()
    {
        PipeProcessGateEnvelopeStore throwing = new(
            new ThrowingTransport(new IOException("pipe unavailable")),
            new FixedRequestIds());
        PipeProcessGateEnvelopeStore malformed = new(
            new ScriptedTransport(_ => "not-json"u8.ToArray()),
            new FixedRequestIds());

        ProcessGateEnvelopeLoadResult first = await throwing.LoadAsync();
        ProcessGateEnvelopeLoadResult second = await malformed.LoadAsync();

        Assert.Equal(ProcessGateStoreLoadStatus.Unavailable, first.Status);
        Assert.Equal(ProcessGateStoreLoadStatus.Unavailable, second.Status);
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        PipeProcessGateEnvelopeStore store = new(
            new ThrowingTransport(new OperationCanceledException(cancellation.Token)),
            new FixedRequestIds());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store
            .LoadAsync(cancellation.Token)
            .AsTask());
    }

    [Fact]
    public async Task OversizedCompleteEnvelope_ReturnsUnavailableBeforeTransport()
    {
        ProcessGateEnvelope oversized = OversizedEnvelope();
        ScriptedTransport transport = new(_ => throw new InvalidOperationException(
            "Transport must not be called for an oversized envelope."));
        PipeProcessGateEnvelopeStore store = new(transport, new FixedRequestIds());

        ProcessGateEnvelopeSaveResult result = await store.CompareExchangeAsync(0, oversized);

        Assert.Equal(ProcessGateStoreSaveStatus.Unavailable, result.Status);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task ResponseAboveProtocolFrameLimit_FailsOpenBeforeParsing()
    {
        byte[] oversized = new byte[NightGateProtocol.MaximumBodyBytes + 1];
        PipeProcessGateEnvelopeStore store = new(
            new ScriptedTransport(_ => oversized),
            new FixedRequestIds());

        ProcessGateEnvelopeLoadResult result = await store.LoadAsync();

        Assert.Equal(ProcessGateStoreLoadStatus.Unavailable, result.Status);
    }

    private static ProcessGateEnvelope SimpleEnvelope(long revision) =>
        ProcessGateEnvelope.Empty with { Revision = revision };

    private static ProcessGateEnvelope OversizedEnvelope()
    {
        DateTimeOffset now = new(2026, 7, 7, 0, 10, 0, TimeSpan.Zero);
        ImmutableDictionary<ProcessInstanceKey, ProcessKnownInstance>.Builder known =
            ImmutableDictionary.CreateBuilder<ProcessInstanceKey, ProcessKnownInstance>();
        for (int index = 1; index <= 900; index++)
        {
            ProcessInstanceKey key = new(index, now.AddTicks(-index).UtcTicks);
            ObservedProcessIdentity identity = new(
                key,
                now.AddTicks(-index),
                $@"C:\Games\LongFolderName{index:D4}\Game.exe",
                "S-1-5-21-1000",
                2);
            known.Add(key, new(identity, ParentLink.None));
        }

        ProcessGateState state = new(
            new DateOnly(2026, 7, 6),
            now,
            now.AddHours(8),
            false,
            "large-state",
            ImmutableDictionary<string, ProcessRuleGateState>.Empty
                .WithComparers(StringComparer.OrdinalIgnoreCase)
                .Add("Game", new("Game", now.AddMinutes(-5), false)),
            known.ToImmutable(),
            ImmutableDictionary<ProcessInstanceKey, string>.Empty,
            ImmutableDictionary<ProcessInstanceKey, TemporaryProcessGrant>.Empty,
            ImmutableHashSet<ProcessInstanceKey>.Empty,
            null,
            null,
            null,
            ImmutableHashSet<ProcessOverrideIdentity>.Empty,
            "epoch",
            null,
            true,
            false);
        return ProcessGateEnvelope.Empty with
        {
            Revision = 1,
            ReducerState = state,
        };
    }

    private static byte[] ResponseForRequest(
        ReadOnlyMemory<byte> request,
        string responseType,
        string status,
        ProcessPersistenceSlot? slot = null,
        long? version = null,
        string? payload = null,
        object? record = null)
    {
        using JsonDocument requestDocument = JsonDocument.Parse(request);
        string requestId = requestDocument.RootElement.GetProperty("requestId").GetString()!;
        object? responseRecord = record;
        JsonDocument? payloadDocument = null;
        try
        {
            if (payload is not null && slot is { } actualSlot && version is { } actualVersion)
            {
                payloadDocument = JsonDocument.Parse(payload);
                responseRecord = new
                {
                    slot = ProcessPersistenceLimits.GetSlotToken(actualSlot),
                    schemaVersion = 1,
                    version = actualVersion,
                    payload = payloadDocument.RootElement,
                };
            }

            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1,
                type = responseType,
                requestId,
                payload = new
                {
                    status = status is "unavailable" or "corrupt" ? "degraded" : "success",
                    data = new { status, record = responseRecord },
                },
            });
        }
        finally
        {
            payloadDocument?.Dispose();
        }
    }

    private sealed class FixedRequestIds : IProtocolRequestIdSource
    {
        private int _next;

        public string NextRequestId() => $"process-{Interlocked.Increment(ref _next)}";
    }

    private sealed class ScriptedTransport(
        Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>> response) : INightGatePipeTransport
    {
        public List<ReadOnlyMemory<byte>> Requests { get; } = [];

        public ValueTask<ReadOnlyMemory<byte>> ExchangeAsync(
            ReadOnlyMemory<byte> requestUtf8,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(requestUtf8.ToArray());
            return ValueTask.FromResult(response(requestUtf8));
        }
    }

    private sealed class ThrowingTransport(Exception exception) : INightGatePipeTransport
    {
        public ValueTask<ReadOnlyMemory<byte>> ExchangeAsync(
            ReadOnlyMemory<byte> requestUtf8,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<ReadOnlyMemory<byte>>(exception);
    }
}
