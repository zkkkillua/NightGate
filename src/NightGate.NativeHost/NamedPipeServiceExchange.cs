using System.IO.Pipes;
using System.Security.Principal;
using NightGate.Protocol;

namespace NightGate.NativeHost;

internal sealed record NativeHostPipeOptions(
    TimeSpan ConnectTimeout,
    TimeSpan WriteTimeout,
    TimeSpan ReadTimeout)
{
    public static NativeHostPipeOptions Default { get; } = new(
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(3));
}

internal sealed class NamedPipeServiceExchange(
    NativeHostPipeOptions? options = null) : IServicePipeExchange
{
    internal const string PipeName = "NightGateService";
    private readonly NativeHostPipeOptions _options = Validate(
        options ?? NativeHostPipeOptions.Default);

    public async ValueTask<ReadOnlyMemory<byte>> ExchangeAsync(
        ReadOnlyMemory<byte> requestUtf8,
        CancellationToken cancellationToken = default)
    {
        if (requestUtf8.Length > NightGateProtocol.MaximumBodyBytes)
        {
            throw new InvalidDataException("Service request exceeds the allowed size.");
        }

        await using NamedPipeClientStream pipe = new(
            ".",
            PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Impersonation);
        await WithDeadlineAsync(
                token => new ValueTask(pipe.ConnectAsync(token)),
                _options.ConnectTimeout,
                "connect",
                cancellationToken)
            .ConfigureAwait(false);
        await WithDeadlineAsync(
                token => ProtocolFraming.WriteFrameAsync(pipe, requestUtf8, token),
                _options.WriteTimeout,
                "write",
                cancellationToken)
            .ConfigureAwait(false);
        return await WithDeadlineAsync(
                token => ProtocolFraming.ReadFrameAsync(pipe, token),
                _options.ReadTimeout,
                "read",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static NativeHostPipeOptions Validate(NativeHostPipeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ConnectTimeout <= TimeSpan.Zero
            || options.WriteTimeout <= TimeSpan.Zero
            || options.ReadTimeout <= TimeSpan.Zero
            || options.ConnectTimeout.TotalMilliseconds > int.MaxValue
            || options.WriteTimeout.TotalMilliseconds > int.MaxValue
            || options.ReadTimeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        return options;
    }

    private static async ValueTask<T> WithDeadlineAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        TimeSpan timeout,
        string operationName,
        CancellationToken callerCancellation)
    {
        using CancellationTokenSource deadline =
            CancellationTokenSource.CreateLinkedTokenSource(callerCancellation);
        deadline.CancelAfter(timeout);
        try
        {
            return await operation(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (!callerCancellation.IsCancellationRequested
                && deadline.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Named-pipe {operationName} deadline elapsed.",
                exception);
        }
    }

    private static async ValueTask WithDeadlineAsync(
        Func<CancellationToken, ValueTask> operation,
        TimeSpan timeout,
        string operationName,
        CancellationToken callerCancellation)
    {
        using CancellationTokenSource deadline =
            CancellationTokenSource.CreateLinkedTokenSource(callerCancellation);
        deadline.CancelAfter(timeout);
        try
        {
            await operation(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (!callerCancellation.IsCancellationRequested
                && deadline.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Named-pipe {operationName} deadline elapsed.",
                exception);
        }
    }
}
