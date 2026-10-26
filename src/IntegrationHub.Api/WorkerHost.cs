namespace IntegrationHub.Api;

// Phase 1 establishes the combined process. Run claiming and sync execution arrive in Phases 3-4.
public sealed class WorkerHost(ILogger<WorkerHost> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Worker host started; synchronization is disabled until the execution engine is implemented.");
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        logger.LogInformation("Worker host stopped.");
    }
}
