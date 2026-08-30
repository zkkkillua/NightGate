using System.Buffers.Binary;

namespace NightGate.Protocol;

public static class ProtocolFraming
{
    public static async ValueTask<ReadOnlyMemory<byte>> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] prefix = new byte[NightGateProtocol.LengthPrefixBytes];
        await ReadExactlyAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is < 0 or > NightGateProtocol.MaximumBodyBytes)
        {
            throw new InvalidDataException("Protocol frame length is outside the allowed range.");
        }

        byte[] body = new byte[length];
        await ReadExactlyAsync(stream, body, cancellationToken).ConfigureAwait(false);
        return body;
    }

    public static async ValueTask WriteFrameAsync(
        Stream stream,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (body.Length > NightGateProtocol.MaximumBodyBytes)
        {
            throw new InvalidDataException("Protocol frame exceeds the allowed size.");
        }

        byte[] prefix = new byte[NightGateProtocol.LengthPrefixBytes];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, body.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream
                .ReadAsync(buffer[offset..], cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }
    }
}
