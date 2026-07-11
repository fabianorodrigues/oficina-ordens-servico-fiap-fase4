using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Oficina.OrdensServico.Infrastructure.Messaging;

internal abstract class SqsBackgroundService(ILogger logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ExecuteOnce(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Messaging background service failed.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    protected abstract Task ExecuteOnce(CancellationToken ct);
}
