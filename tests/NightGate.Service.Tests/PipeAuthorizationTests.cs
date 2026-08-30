using System.Text;
using NightGate.Core;
using NightGate.Service;

namespace NightGate.Service.Tests;

public sealed class PipeAuthorizationTests
{
    [Theory]
    [InlineData("S-1-5-21-1-2-3-1001", true)]
    [InlineData("S-1-5-19", true)]
    [InlineData("S-1-5-21-1-2-3-1002", false)]
    [InlineData("DOMAIN\\configured", false)]
    public void ConfiguredAuthorizer_AllowsOnlyConfiguredUserOrServiceIdentity(
        string peerSid,
        bool expected)
    {
        ConfiguredPipePeerAuthorizer authorizer = new(
            "S-1-5-21-1-2-3-1001",
            "S-1-5-19");

        bool result = authorizer.IsAuthorized(new(peerSid));

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task Adapter_UnauthorizedPeerIsRejectedBeforeMessageReadOrCommandExecution()
    {
        StubCommandHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);
        FakeIdentityProvider identityProvider = new(new("S-1-5-21-1-2-3-1002"));
        ConfiguredPipePeerAuthorizer authorizer = new(
            "S-1-5-21-1-2-3-1001",
            "S-1-5-19");
        FakePipeConnection connection = new(ValidStatusMessage());
        NamedPipeServerAdapter adapter = new(identityProvider, authorizer, dispatcher);

        PipeConnectionResult result = await adapter.HandleConnectionAsync(connection);

        Assert.Equal(PipeConnectionStatus.Unauthorized, result.Status);
        Assert.Equal(0, connection.ReadCount);
        Assert.Equal(0, connection.WriteCount);
        Assert.Equal(0, handler.ExecutionCount);
    }

    [Theory]
    [InlineData("S-1-5-21-1-2-3-1001")]
    [InlineData("S-1-5-19")]
    public async Task Adapter_RealAuthorizationDecisionAllowsConfiguredIdentities(string peerSid)
    {
        StubCommandHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);
        FakeIdentityProvider identityProvider = new(new(peerSid));
        ConfiguredPipePeerAuthorizer authorizer = new(
            "S-1-5-21-1-2-3-1001",
            "S-1-5-19");
        FakePipeConnection connection = new(ValidStatusMessage());
        NamedPipeServerAdapter adapter = new(identityProvider, authorizer, dispatcher);

        PipeConnectionResult result = await adapter.HandleConnectionAsync(connection);

        Assert.Equal(PipeConnectionStatus.Processed, result.Status);
        Assert.Equal(1, connection.ReadCount);
        Assert.Equal(1, connection.WriteCount);
        Assert.Equal(1, handler.ExecutionCount);
        Assert.Contains("pipe-request", Encoding.UTF8.GetString(connection.Response.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adapter_HandlerFailureWritesTypedErrorAndReportsDegraded()
    {
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), new ThrowingHandler());
        FakeIdentityProvider identityProvider = new(new("S-1-5-21-1-2-3-1001"));
        ConfiguredPipePeerAuthorizer authorizer = new(
            "S-1-5-21-1-2-3-1001",
            "S-1-5-19");
        FakePipeConnection connection = new(ValidStatusMessage());
        NamedPipeServerAdapter adapter = new(identityProvider, authorizer, dispatcher);

        PipeConnectionResult result = await adapter.HandleConnectionAsync(connection);

        Assert.Equal(PipeConnectionStatus.Degraded, result.Status);
        Assert.Contains("commandFailed", Encoding.UTF8.GetString(connection.Response.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adapter_StalledAuthorizedClientTimesOutAndReportsDegraded()
    {
        StubCommandHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);
        FakeIdentityProvider identityProvider = new(new("S-1-5-21-1-2-3-1001"));
        ConfiguredPipePeerAuthorizer authorizer = new(
            "S-1-5-21-1-2-3-1001",
            "S-1-5-19");
        StalledPipeConnection connection = new();
        NamedPipeServerAdapter adapter = new(
            identityProvider,
            authorizer,
            dispatcher,
            new ImmediatePipeConnectionDeadline());

        PipeConnectionResult result = await adapter.HandleConnectionAsync(connection);

        Assert.Equal(PipeConnectionStatus.Degraded, result.Status);
        Assert.Equal(0, handler.ExecutionCount);
    }

    private static byte[] ValidStatusMessage() => Encoding.UTF8.GetBytes(
        "{\"version\":1,\"type\":\"getStatus\",\"requestId\":\"pipe-request\",\"payload\":{}}");

    private sealed class FakeIdentityProvider(PipePeerIdentity? identity) : IPipePeerIdentityProvider
    {
        public ValueTask<PipePeerIdentity?> GetIdentityAsync(
            IPipeConnection connection,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(identity);
    }

    private sealed class FakePipeConnection(byte[] request) : IPipeConnection
    {
        public int ReadCount { get; private set; }

        public int WriteCount { get; private set; }

        public ReadOnlyMemory<byte> Response { get; private set; }

        public ValueTask<ReadOnlyMemory<byte>> ReadMessageAsync(
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(request);
        }

        public ValueTask WriteMessageAsync(
            ReadOnlyMemory<byte> message,
            CancellationToken cancellationToken = default)
        {
            WriteCount++;
            Response = message.ToArray();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubCommandHandler : IProtocolCommandHandler
    {
        public int ExecutionCount { get; private set; }

        public ValueTask<ProtocolCommandResult> ExecuteAsync(
            ServiceCommand command,
            CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            return ValueTask.FromResult(ProtocolCommandResult.Success(new { ready = true }));
        }
    }

    private sealed class ThrowingHandler : IProtocolCommandHandler
    {
        public ValueTask<ProtocolCommandResult> ExecuteAsync(
            ServiceCommand command,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated handler failure");
    }

    private sealed class StalledPipeConnection : IPipeConnection
    {
        public async ValueTask<ReadOnlyMemory<byte>> ReadMessageAsync(
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return ReadOnlyMemory<byte>.Empty;
        }

        public ValueTask WriteMessageAsync(
            ReadOnlyMemory<byte> message,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ImmediatePipeConnectionDeadline : IPipeConnectionDeadline
    {
        public CancellationTokenSource Create(CancellationToken serviceToken)
        {
            CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(serviceToken);
            source.Cancel();
            return source;
        }
    }
}
