using FreightSnapIngestService.Options;
using Microsoft.Extensions.Options;

namespace FreightSnapIngestService.Ingestion;

public class IngestWorker(
    IngestPipeline pipeline,
    IOptions<FreightIngestOptions> options,
    TimeProvider timeProvider,
    ILogger<IngestWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds), timeProvider);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await pipeline.ProcessOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Ingest poll cycle failed.");
            }
        }
    }
}
