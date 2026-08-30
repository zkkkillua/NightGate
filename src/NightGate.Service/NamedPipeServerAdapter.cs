using System.Security.Principal;

namespace NightGate.Service;

public sealed record PipePeerIdentity(string Sid);

public interface IPipePeerAuthorizer
{
    bool IsAuthorized(PipePeerIdentity identity);
}

public interface IPipePeerIdentityProvider
{
    ValueTask<PipePeerIdentity?> GetIdentityAsync(
        IPipeConnection connection,
        CancellationToken cancellationToken = default);
}

public interface IPipeConnection : IAsyncDisposable
{
    ValueTask<ReadOnlyMemory<byte>> ReadMessageAsync(
        CancellationToken cancellationToken = default);

    ValueTask WriteMessageAsync(
        ReadOnlyMemory<byte> message,
        CancellationToken cancellationToken = default);
}

public interface INamedPipeServerFactory
{
    ValueTask<IPipeConnection> AcceptConnectionAsync(
        CancellationToken cancellationToken = default);
}

public interface IPipeConnectionDeadline
{
    CancellationTokenSource Create(CancellationToken serviceToken);
}

public sealed class PipeConnectionDeadline(TimeSpan timeout) : IPipeConnectionDeadline
{
    public static PipeConnectionDeadline Default { get; } = new(TimeSpan.FromSeconds(5));

    public CancellationTokenSource Create(CancellationToken serviceToken)
    {
        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(serviceToken);
        source.CancelAfter(timeout);
        return source;
    }
}

public enum PipeConnectionStatus
{
    Processed,
    Unauthorized,
    Degraded,
}

public sealed record PipeConnectionResult(
    PipeConnectionStatus Status,
    string? DegradationCode = null);

public sealed class ConfiguredPipePeerAuthorizer : IPipePeerAuthorizer
{
    private readonly string _configuredUserSid;
    private readonly string _serviceIdentitySid;

    public ConfiguredPipePeerAuthorizer(
        string configuredUserSid,
        string serviceIdentitySid)
    {
        _configuredUserSid = NormalizeSid(configuredUserSid);
        _serviceIdentitySid = NormalizeSid(serviceIdentitySid);
    }

    public bool IsAuthorized(PipePeerIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        string peerSid;
        try
        {
            peerSid = NormalizeSid(identity.Sid);
        }
        catch (ArgumentException)
        {
            return false;
        }

        return string.Equals(peerSid, _configuredUserSid, StringComparison.Ordinal)
            || string.Equals(peerSid, _serviceIdentitySid, StringComparison.Ordinal);
    }

    private static string NormalizeSid(string sid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sid);
        return new SecurityIdentifier(sid).Value;
    }
}

public sealed class NamedPipeServerAdapter(
    IPipePeerIdentityProvider identityProvider,
    IPipePeerAuthorizer authorizer,
    ServiceCommandDispatcher dispatcher,
    IPipeConnectionDeadline? connectionDeadline = null)
{
    private readonly IPipeConnectionDeadline _connectionDeadline =
        connectionDeadline ?? PipeConnectionDeadline.Default;

    public async ValueTask<PipeConnectionResult> RunOnceAsync(
        INamedPipeServerFactory serverFactory,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using IPipeConnection connection = await serverFactory
                .AcceptConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            return await HandleConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(PipeConnectionStatus.Degraded, "pipe-unavailable");
        }
    }

    public async ValueTask<PipeConnectionResult> HandleConnectionAsync(
        IPipeConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        try
        {
            PipePeerIdentity? identity = await identityProvider
                .GetIdentityAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            if (identity is null || !authorizer.IsAuthorized(identity))
            {
                return new(PipeConnectionStatus.Unauthorized);
            }

            using CancellationTokenSource deadline = _connectionDeadline.Create(cancellationToken);
            ReadOnlyMemory<byte> request = await connection
                .ReadMessageAsync(deadline.Token)
                .ConfigureAwait(false);
            ProtocolDispatchResult dispatched = await dispatcher
                .DispatchAsync(request, deadline.Token)
                .ConfigureAwait(false);
            await connection
                .WriteMessageAsync(dispatched.ResponseUtf8, deadline.Token)
                .ConfigureAwait(false);
            return dispatched.IsDegraded
                ? new(PipeConnectionStatus.Degraded, "command-degraded")
                : new(PipeConnectionStatus.Processed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(PipeConnectionStatus.Degraded, "pipe-failure");
        }
    }
}
