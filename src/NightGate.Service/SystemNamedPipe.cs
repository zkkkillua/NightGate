using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using NightGate.Protocol;

namespace NightGate.Service;

public interface INamedPipePeerIdentitySource
{
    string GetPeerSid();
}

public sealed class WindowsPipePeerIdentityProvider : IPipePeerIdentityProvider
{
    public ValueTask<PipePeerIdentity?> GetIdentityAsync(
        IPipeConnection connection,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (connection is not INamedPipePeerIdentitySource source)
        {
            return ValueTask.FromResult<PipePeerIdentity?>(null);
        }

        try
        {
            string sid = source.GetPeerSid();
            return ValueTask.FromResult<PipePeerIdentity?>(
                string.IsNullOrWhiteSpace(sid) ? null : new(sid));
        }
        catch (Exception)
        {
            return ValueTask.FromResult<PipePeerIdentity?>(null);
        }
    }
}

[SupportedOSPlatform("windows")]
public sealed class SystemNamedPipeServerFactory : INamedPipeServerFactory
{
    private readonly string _pipeName;

    public SystemNamedPipeServerFactory(string pipeName, string configuredUserSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredUserSid);
        _pipeName = pipeName;
        ConfiguredUserSid = new SecurityIdentifier(configuredUserSid).Value;
    }

    internal string ConfiguredUserSid { get; }

    public async ValueTask<IPipeConnection> AcceptConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        NamedPipeServerStream stream = NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            SystemNamedPipeSecurity.Create(ConfiguredUserSid),
            HandleInheritability.None,
            additionalAccessRights: (PipeAccessRights)0);

        try
        {
            await stream.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            return new SystemNamedPipeConnection(stream);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

[SupportedOSPlatform("windows")]
public sealed class SystemNamedPipeConnection(NamedPipeServerStream stream) :
    IPipeConnection,
    INamedPipePeerIdentitySource
{
    public async ValueTask<ReadOnlyMemory<byte>> ReadMessageAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await ProtocolFraming
                .ReadFrameAsync(stream, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return new byte[NightGateProtocol.MaximumBodyBytes + 1];
        }
    }

    public async ValueTask WriteMessageAsync(
        ReadOnlyMemory<byte> message,
        CancellationToken cancellationToken = default)
    {
        await ProtocolFraming
            .WriteFrameAsync(stream, message, cancellationToken)
            .ConfigureAwait(false);
    }

    public string GetPeerSid()
    {
        string? sid = null;
        stream.RunAsClient(() =>
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            sid = identity.User?.Value;
        });
        return sid ?? throw new InvalidOperationException("The connected pipe client has no SID.");
    }

    public ValueTask DisposeAsync() => stream.DisposeAsync();

}
