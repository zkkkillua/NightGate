using System.Buffers.Binary;
using NightGate.NativeHost;

namespace NightGate.NativeHost.Tests;

public sealed class ChromeNativeMessageFramingTests
{
    [Fact]
    public async Task ReadAsync_ReassemblesPartialPrefixAndBody()
    {
        byte[] body = "{\"version\":1}"u8.ToArray();
        byte[] frame = Frame(body);
        await using Stream input = new ChunkedReadStream(frame, 1, 2, 1, 3);

        NativeMessageReadResult result = await ChromeNativeMessageFraming.ReadAsync(input);

        Assert.Equal(NativeMessageReadStatus.Message, result.Status);
        Assert.Equal(body, result.Body.ToArray());
    }

    [Fact]
    public async Task ReadAsync_CleanEofBeforePrefix_IsNotMalformed()
    {
        NativeMessageReadResult result = await ChromeNativeMessageFraming.ReadAsync(
            new MemoryStream());

        Assert.Equal(NativeMessageReadStatus.EndOfStream, result.Status);
        Assert.True(result.Body.IsEmpty);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ReadAsync_TruncatedPrefix_IsInvalid(int prefixBytes)
    {
        NativeMessageReadResult result = await ChromeNativeMessageFraming.ReadAsync(
            new MemoryStream(new byte[prefixBytes]));

        Assert.Equal(NativeMessageReadStatus.Invalid, result.Status);
        Assert.True(result.Body.IsEmpty);
    }

    [Fact]
    public async Task ReadAsync_TruncatedBody_IsInvalid()
    {
        byte[] frame = Frame("{}"u8.ToArray());
        Array.Resize(ref frame, frame.Length - 1);

        NativeMessageReadResult result = await ChromeNativeMessageFraming.ReadAsync(
            new MemoryStream(frame));

        Assert.Equal(NativeMessageReadStatus.Invalid, result.Status);
        Assert.True(result.Body.IsEmpty);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65_537)]
    [InlineData(int.MaxValue)]
    public async Task ReadAsync_InvalidLength_IsRejectedBeforeAllocation(int length)
    {
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, length);

        NativeMessageReadResult result = await ChromeNativeMessageFraming.ReadAsync(
            new MemoryStream(prefix));

        Assert.Equal(NativeMessageReadStatus.Invalid, result.Status);
        Assert.True(result.Body.IsEmpty);
    }

    [Fact]
    public async Task ReadAsync_ExactMaximumBody_IsAccepted()
    {
        byte[] body = new byte[ChromeNativeMessageFraming.MaximumBodyBytes];
        NativeMessageReadResult result = await ChromeNativeMessageFraming.ReadAsync(
            new MemoryStream(Frame(body)));

        Assert.Equal(NativeMessageReadStatus.Message, result.Status);
        Assert.Equal(ChromeNativeMessageFraming.MaximumBodyBytes, result.Body.Length);
    }

    [Fact]
    public async Task WriteAsync_EmitsOneLittleEndianFrameAndFlushes()
    {
        byte[] body = "{\"accepted\":true}"u8.ToArray();
        TrackingMemoryStream output = new();

        await ChromeNativeMessageFraming.WriteAsync(output, body);

        byte[] frame = output.ToArray();
        Assert.Equal(body.Length, BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(0, 4)));
        Assert.Equal(body, frame.AsSpan(4).ToArray());
        Assert.True(output.WasFlushed);
    }

    [Fact]
    public async Task WriteAsync_RejectsOversizedBodyWithoutWriting()
    {
        MemoryStream output = new();

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await ChromeNativeMessageFraming.WriteAsync(
                output,
                new byte[ChromeNativeMessageFraming.MaximumBodyBytes + 1]));

        Assert.Equal(0, output.Length);
    }

    private static byte[] Frame(byte[] body)
    {
        byte[] frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    private sealed class TrackingMemoryStream : MemoryStream
    {
        public bool WasFlushed { get; private set; }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            WasFlushed = true;
            return base.FlushAsync(cancellationToken);
        }
    }

    private sealed class ChunkedReadStream(byte[] value, params int[] chunks) : Stream
    {
        private int _offset;
        private int _chunkIndex;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => value.Length;
        public override long Position { get => _offset; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (_offset == value.Length)
            {
                return 0;
            }

            int chunk = chunks[_chunkIndex++ % chunks.Length];
            int count = Math.Min(Math.Min(chunk, buffer.Length), value.Length - _offset);
            value.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
