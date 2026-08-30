using System.Buffers.Binary;
using System.Security.Principal;
using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class NamedPipeNightGateTransportTests
{
    private static readonly NightGatePipeTransportOptions FastTimeouts = new(
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(50));

    [Fact]
    public void ProductionFactory_UsesSecurityImpersonationSoServiceCanReadClientSid()
    {
        NamedPipeClientConnectionFactory factory = new();

        Assert.Equal(TokenImpersonationLevel.Impersonation, factory.ImpersonationLevel);
    }

    [Fact]
    public async Task Exchange_UsesSharedLengthFramingForRequestAndResponse()
    {
        byte[] responseBody = [9, 8, 7];
        DuplexMemoryStream stream = new(Frame(responseBody));
        NamedPipeNightGateTransport transport = new(
            new FixedConnectionFactory(stream),
            FastTimeouts);

        ReadOnlyMemory<byte> response = await transport.ExchangeAsync(new byte[] { 1, 2, 3, 4 });

        Assert.Equal(responseBody, response.ToArray());
        byte[] written = stream.Written.ToArray();
        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(written));
        Assert.Equal([1, 2, 3, 4], written.AsSpan(4).ToArray());
        Assert.True(stream.IsDisposed);
    }

    [Fact]
    public async Task ConnectDeadline_ThrowsTimeoutAndCancelsConnectionAttempt()
    {
        BlockingConnectionFactory factory = new();
        NamedPipeNightGateTransport transport = new(factory, FastTimeouts);

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await transport.ExchangeAsync(new byte[] { 1 }));

        Assert.True(factory.CancellationObserved);
    }

    [Fact]
    public async Task WriteDeadline_ThrowsTimeoutAndDisposesConnection()
    {
        BlockingWriteStream stream = new();
        NamedPipeNightGateTransport transport = new(
            new FixedConnectionFactory(stream),
            FastTimeouts);

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await transport.ExchangeAsync(new byte[] { 1 }));

        Assert.True(stream.CancellationObserved);
        Assert.True(stream.IsDisposed);
    }

    [Fact]
    public async Task ReadDeadline_ThrowsTimeoutAndDisposesConnection()
    {
        BlockingReadStream stream = new();
        NamedPipeNightGateTransport transport = new(
            new FixedConnectionFactory(stream),
            FastTimeouts);

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await transport.ExchangeAsync(new byte[] { 1 }));

        Assert.True(stream.CancellationObserved);
        Assert.True(stream.IsDisposed);
    }

    [Fact]
    public async Task CallerCancellation_RemainsCancellationRatherThanTimeout()
    {
        BlockingConnectionFactory factory = new();
        NamedPipeNightGateTransport transport = new(factory, FastTimeouts);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await transport.ExchangeAsync(new byte[] { 1 }, cancellation.Token));
    }

    [Fact]
    public async Task DisconnectDuringRead_IsReportedAsEndOfStream()
    {
        DuplexMemoryStream stream = new([]);
        NamedPipeNightGateTransport transport = new(
            new FixedConnectionFactory(stream),
            FastTimeouts);

        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
            await transport.ExchangeAsync(new byte[] { 1 }));
    }

    private static byte[] Frame(byte[] body)
    {
        byte[] frame = new byte[sizeof(int) + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, sizeof(int));
        return frame;
    }

    private sealed class FixedConnectionFactory(Stream stream) : INightGatePipeConnectionFactory
    {
        public ValueTask<Stream> ConnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(stream);
        }
    }

    private sealed class BlockingConnectionFactory : INightGatePipeConnectionFactory
    {
        public bool CancellationObserved { get; private set; }

        public async ValueTask<Stream> ConnectAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }

            throw new InvalidOperationException();
        }
    }

    private sealed class DuplexMemoryStream(byte[] response) : Stream
    {
        private readonly MemoryStream _read = new(response);

        public MemoryStream Written { get; } = new();

        public bool IsDisposed { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _read.ReadAsync(buffer, cancellationToken);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            Written.WriteAsync(buffer, cancellationToken);

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class BlockingWriteStream : Stream
    {
        public bool CancellationObserved { get; private set; }
        public bool IsDisposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class BlockingReadStream : Stream
    {
        public bool CancellationObserved { get; private set; }
        public bool IsDisposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }

            return 0;
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
