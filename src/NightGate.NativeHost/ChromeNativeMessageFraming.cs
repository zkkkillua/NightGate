using System.Buffers.Binary;

namespace NightGate.NativeHost;

internal enum NativeMessageReadStatus
{
    Message,
    EndOfStream,
    Invalid,
}

internal readonly record struct NativeMessageReadResult(
    NativeMessageReadStatus Status,
    ReadOnlyMemory<byte> Body);

internal static class ChromeNativeMessageFraming
{
    public const int MaximumBodyBytes = 65_536;
    private const int LengthPrefixBytes = sizeof(int);

    public static async ValueTask<NativeMessageReadResult> ReadAsync(
        Stream input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        byte[] prefix = new byte[LengthPrefixBytes];
        int prefixBytes = await ReadUpToAsync(input, prefix, cancellationToken)
            .ConfigureAwait(false);
        if (prefixBytes == 0)
        {
            return new(NativeMessageReadStatus.EndOfStream, ReadOnlyMemory<byte>.Empty);
        }

        if (prefixBytes != LengthPrefixBytes)
        {
            return new(NativeMessageReadStatus.Invalid, ReadOnlyMemory<byte>.Empty);
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is < 0 or > MaximumBodyBytes)
        {
            return new(NativeMessageReadStatus.Invalid, ReadOnlyMemory<byte>.Empty);
        }

        byte[] body = new byte[length];
        if (await ReadUpToAsync(input, body, cancellationToken).ConfigureAwait(false) != length)
        {
            return new(NativeMessageReadStatus.Invalid, ReadOnlyMemory<byte>.Empty);
        }

        return new(NativeMessageReadStatus.Message, body);
    }

    public static async ValueTask WriteAsync(
        Stream output,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (body.Length > MaximumBodyBytes)
        {
            throw new InvalidDataException("Native message body exceeds the allowed size.");
        }

        byte[] prefix = new byte[LengthPrefixBytes];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, body.Length);
        await output.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<int> ReadUpToAsync(
        Stream input,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await input
                .ReadAsync(buffer[offset..], cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        return offset;
    }
}
