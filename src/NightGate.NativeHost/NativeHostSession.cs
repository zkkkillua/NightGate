using System.Security.Cryptography;
using System.Text;

namespace NightGate.NativeHost;

internal enum NativeHostExitCode
{
    Success = 0,
    InvalidInput = 2,
    BackendUnavailable = 3,
}

internal sealed record NativeHeartbeatObservation(
    string ExtensionId,
    string ExtensionVersion,
    string ProfileTokenSha256,
    long PolicyRevision,
    bool IncognitoAllowed,
    bool ProtectionReady);

internal interface INativeHostBackend
{
    ValueTask<ChromePolicyPayload> GetPolicyAsync(
        CancellationToken cancellationToken = default);

    ValueTask<bool> HeartbeatAsync(
        NativeHeartbeatObservation observation,
        CancellationToken cancellationToken = default);

    ValueTask<bool> RecordEventAsync(
        BrowserPrivacyEvent privacyEvent,
        CancellationToken cancellationToken = default);
}

internal static class NativeHostSession
{
    private const int MaximumRequestsPerSession = 256;
    private const string ExtensionId = "eefgemhlhbdodhlgjmicnoifhclhdgmm";

    public static async ValueTask<NativeHostExitCode> RunAsync(
        Stream input,
        Stream output,
        INativeHostBackend backend,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(backend);

        string? sessionProfileToken = null;
        HashSet<string> requestIds = new(StringComparer.Ordinal);
        int requestCount = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                NativeMessageReadResult frame = await ChromeNativeMessageFraming
                    .ReadAsync(input, cancellationToken)
                    .ConfigureAwait(false);
                if (frame.Status == NativeMessageReadStatus.EndOfStream)
                {
                    return NativeHostExitCode.Success;
                }
                if (frame.Status != NativeMessageReadStatus.Message
                    || ++requestCount > MaximumRequestsPerSession
                    || !NativeHostMessageCodec.TryDecode(frame.Body, out NativeHostRequest? request)
                    || request is null
                    || (sessionProfileToken is not null
                        && !string.Equals(
                            sessionProfileToken,
                            request.ProfileToken,
                            StringComparison.Ordinal))
                    || !requestIds.Add(request.RequestId))
                {
                    return NativeHostExitCode.InvalidInput;
                }
                sessionProfileToken ??= request.ProfileToken;

                byte[] response = request.Kind switch
                {
                    NativeHostRequestKind.GetPolicy =>
                        NativeHostMessageCodec.EncodePolicy(
                            request,
                            await backend.GetPolicyAsync(cancellationToken).ConfigureAwait(false)),
                    NativeHostRequestKind.Heartbeat =>
                        NativeHostMessageCodec.EncodeAcknowledgement(
                            request,
                            await backend.HeartbeatAsync(
                                    CreateHeartbeatObservation(request),
                                    cancellationToken)
                                .ConfigureAwait(false)),
                    NativeHostRequestKind.MediaState
                        or NativeHostRequestKind.NavigationAttempt =>
                        NativeHostMessageCodec.EncodeAcknowledgement(
                            request,
                            await backend.RecordEventAsync(
                                    request.PrivacyEvent!,
                                    cancellationToken)
                                .ConfigureAwait(false)),
                    _ => throw new InvalidDataException("Unknown native request kind."),
                };
                await ChromeNativeMessageFraming
                    .WriteAsync(output, response, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                return NativeHostExitCode.BackendUnavailable;
            }
        }
    }

    private static NativeHeartbeatObservation CreateHeartbeatObservation(
        NativeHostRequest request)
    {
        NativeHeartbeatPayload heartbeat = request.Heartbeat
            ?? throw new InvalidDataException("Heartbeat payload is missing.");
        byte[] profileTokenSha256 = SHA256.HashData(
            Encoding.ASCII.GetBytes(request.ProfileToken));
        return new(
            ExtensionId,
            heartbeat.ExtensionVersion,
            Convert.ToHexString(profileTokenSha256).ToLowerInvariant(),
            heartbeat.Revision,
            heartbeat.IncognitoAllowed,
            heartbeat.ProtectionReady);
    }

    private static bool IsFatal(Exception exception) => exception is
        OutOfMemoryException or
        StackOverflowException or
        AccessViolationException;
}
