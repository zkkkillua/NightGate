using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NightGate.Service;

public interface IPolicyMaintenanceDelay
{
    ValueTask DelayAsync(CancellationToken cancellationToken = default);
}

public sealed class NightPolicyWorker(
    IPolicyMaintenanceScheduler scheduler,
    IServiceStatusPublisher statusPublisher,
    IPolicyMaintenanceDelay delay,
    ILogger<NightPolicyWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await scheduler.RefreshAsync(force: true, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                await PublishFailureAsync(exception, stoppingToken).ConfigureAwait(false);
            }

            try
            {
                await delay.DelayAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                await PublishFailureAsync(exception, stoppingToken).ConfigureAwait(false);
                try
                {
                    await Task.Delay(
                            PolicyMaintenanceTiming.WatchdogInterval,
                            stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async ValueTask PublishFailureAsync(
        Exception exception,
        CancellationToken cancellationToken)
    {
        logger.LogError(exception, "NightGate policy maintenance failed; enforcement is disabled.");
        try
        {
            await statusPublisher.PublishAsync(
                ServiceRuntimeStatus.Degraded("policy-maintenance-failure"),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception publishException)
        {
            logger.LogError(publishException, "NightGate failed to publish policy degradation.");
        }
    }
}
