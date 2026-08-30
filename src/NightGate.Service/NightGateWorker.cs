using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NightGate.Service;

public interface IServiceLoopIteration
{
    ValueTask ExecuteAsync(CancellationToken cancellationToken = default);
}

public interface IServiceLoopDelay
{
    ValueTask DelayAsync(CancellationToken cancellationToken = default);
}

public sealed class FixedServiceLoopDelay : IServiceLoopDelay
{
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(1);

    public async ValueTask DelayAsync(CancellationToken cancellationToken = default) =>
        await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
}

public sealed class NightGateWorker(
    IServiceLoopIteration iteration,
    IServiceStatusPublisher statusPublisher,
    IServiceLoopDelay loopDelay,
    ILogger<NightGateWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool retryAfterFailure = false;
            try
            {
                await iteration.ExecuteAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                await PublishFailureAsync(exception, stoppingToken).ConfigureAwait(false);
                retryAfterFailure = true;
            }

            if (!retryAfterFailure)
            {
                continue;
            }

            try
            {
                await loopDelay.DelayAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                await PublishFailureAsync(exception, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask PublishFailureAsync(
        Exception exception,
        CancellationToken cancellationToken)
    {
        logger.LogError(exception, "NightGate service loop boundary failed; enforcement is disabled.");
        try
        {
            await statusPublisher.PublishAsync(
                ServiceRuntimeStatus.Degraded("worker-loop-failure"),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception publishException)
        {
            logger.LogError(publishException, "NightGate failed to publish degraded status.");
        }
    }
}

public sealed class NamedPipeServiceIteration(
    NamedPipeServerAdapter adapter,
    INamedPipeServerFactory serverFactory) : IServiceLoopIteration
{
    public async ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
    {
        PipeConnectionResult result = await adapter
            .RunOnceAsync(serverFactory, cancellationToken)
            .ConfigureAwait(false);
        if (result.Status == PipeConnectionStatus.Degraded)
        {
            throw new IOException(result.DegradationCode ?? "pipe-failure");
        }
    }
}
