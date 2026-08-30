using System.Buffers.Binary;
using NightGate.Protocol;

namespace NightGate.Service.Tests;

public sealed class ProtocolFramingTests
{
    [Fact]
    public void Constants_AreTheVersionOneBoundedLittleEndianContract()
    {
        Assert.Equal(1, NightGateProtocol.Version);
        Assert.Equal(65_536, NightGateProtocol.MaximumBodyBytes);
        Assert.Equal(sizeof(int), NightGateProtocol.LengthPrefixBytes);
        Assert.True(NightGateProtocol.IsValidRequestId("request 1"));
        Assert.True(NightGateProtocol.IsValidRequestId(new string('x', 64)));
        Assert.False(NightGateProtocol.IsValidRequestId(string.Empty));
        Assert.False(NightGateProtocol.IsValidRequestId("   "));
        Assert.False(NightGateProtocol.IsValidRequestId(new string('x', 65)));
        Assert.False(NightGateProtocol.IsValidRequestId("unicode-\u4F60"));
        Assert.False(NightGateProtocol.IsValidRequestId("line\nbreak"));
    }

    [Fact]
    public async Task WriteFrame_UsesExactLittleEndianPrefixAndAllowsBoundary()
    {
        byte[] body = new byte[NightGateProtocol.MaximumBodyBytes];
        body[0] = 0x11;
        body[^1] = 0x22;
        await using MemoryStream stream = new();

        await ProtocolFraming.WriteFrameAsync(stream, body);

        byte[] written = stream.ToArray();
        Assert.Equal(body.Length + sizeof(int), written.Length);
        Assert.Equal(body.Length, BinaryPrimitives.ReadInt32LittleEndian(written.AsSpan(0, 4)));
        Assert.Equal(body, written.AsSpan(4).ToArray());
    }

    [Fact]
    public async Task ReadFrame_ExactReadsPrefixAndBodyAcrossPartialReads()
    {
        byte[] body = [1, 2, 3, 4, 5];
        byte[] frame = Frame(body.Length, body);
        await using Stream stream = new PartialReadStream(frame, maximumReadSize: 1);

        ReadOnlyMemory<byte> actual = await ProtocolFraming.ReadFrameAsync(stream);

        Assert.Equal(body, actual.ToArray());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65_537)]
    public async Task ReadFrame_RejectsNegativeOrOversizeLengthWithoutAllocatingBody(int length)
    {
        await using MemoryStream stream = new(Frame(length, []));

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await ProtocolFraming.ReadFrameAsync(stream));

        Assert.Equal(sizeof(int), stream.Position);
    }

    [Fact]
    public async Task WriteFrame_RejectsOversizeBodyBeforeWriting()
    {
        await using MemoryStream stream = new();
        byte[] body = new byte[NightGateProtocol.MaximumBodyBytes + 1];

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await ProtocolFraming.WriteFrameAsync(stream, body));

        Assert.Equal(0, stream.Length);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 4)]
    [InlineData(4, 3)]
    public async Task ReadFrame_ThrowsEndOfStreamForIncompletePrefixOrBody(
        int availableBytes,
        int declaredLength)
    {
        byte[] frame = Frame(declaredLength, new byte[Math.Max(0, availableBytes - 4)]);
        await using MemoryStream stream = new(frame.AsSpan(0, availableBytes).ToArray());

        await Assert.ThrowsAsync<EndOfStreamException>(
            async () => await ProtocolFraming.ReadFrameAsync(stream));
    }

    [Fact]
    public async Task ReadFrame_HonorsCancellationWhileWaitingForBytes()
    {
        await using Stream stream = new BlockingReadStream();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await ProtocolFraming.ReadFrameAsync(stream, cancellation.Token));
    }

    private static byte[] Frame(int length, byte[] body)
    {
        byte[] frame = new byte[sizeof(int) + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, length);
        body.CopyTo(frame, sizeof(int));
        return frame;
    }

    private sealed class PartialReadStream(byte[] bytes, int maximumReadSize) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(maximumReadSize, buffer.Length)], cancellationToken);
    }

    private sealed class BlockingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
