using Microsoft.Extensions.Options;

namespace VGA.FileUploadWorker;

public sealed class HeartbeatBackgroundService : BackgroundService
{
    private readonly DsMonitorClient _client;
    private readonly DsMonitorOptions _options;
    private readonly ILogger<HeartbeatBackgroundService> _logger;

    public HeartbeatBackgroundService(
        DsMonitorClient client,
        IOptions<DsMonitorOptions> options,
        ILogger<HeartbeatBackgroundService> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_client.IsConfigured)
        {
            _logger.LogInformation("DS Monitor deshabilitado (DsMonitor:Enabled=false o sin DsMonitor:Token).");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.IntervalMinutes));
        _logger.LogInformation("Heartbeat a DS Monitor cada {Minutes} min (integración {Integration}).",
            interval.TotalMinutes, _options.Integration);

        try
        {
            await _client.ReportAsync("ok", "Aplicación iniciada", stoppingToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await _client.ReportAsync("ok", "Proceso activo", stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
