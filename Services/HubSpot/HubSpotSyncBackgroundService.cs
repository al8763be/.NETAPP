using Microsoft.EntityFrameworkCore;
using WebApplication2.Data;

namespace WebApplication2.Services.HubSpot
{
    public class HubSpotSyncBackgroundService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<HubSpotSyncBackgroundService> _logger;

        public HubSpotSyncBackgroundService(
            IServiceScopeFactory scopeFactory,
            ILogger<HubSpotSyncBackgroundService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "HubSpot background sync service started. Scheduled sync runs at the top of every UTC hour.");

            while (!stoppingToken.IsCancellationRequested)
            {
                var nowUtc = DateTime.UtcNow;
                var nextRunUtc = new DateTime(
                    nowUtc.Year,
                    nowUtc.Month,
                    nowUtc.Day,
                    nowUtc.Hour,
                    0,
                    0,
                    DateTimeKind.Utc).AddHours(1);

                var delay = nextRunUtc - nowUtc;
                _logger.LogInformation(
                    "Next scheduled HubSpot sync at {NextRunUtc}.",
                    nextRunUtc);

                try
                {
                    await Task.Delay(delay, stoppingToken);
                    await RunScheduledSyncAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled error while running scheduled HubSpot sync.");
                }
            }
        }

        private async Task RunScheduledSyncAsync(CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<STLForumContext>();
            var options = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<HubSpotOptions>>().Value;

            if (!options.Enabled)
            {
                return;
            }

            var nowUtc = DateTime.UtcNow;
            var hasInFlightRun = await context.HubSpotSyncRuns
                .AsNoTracking()
                .AnyAsync(
                    r => r.Status == "Started" && r.StartedUtc >= nowUtc.AddHours(-2),
                    cancellationToken);

            if (hasInFlightRun)
            {
                _logger.LogInformation("Skipping scheduled HubSpot sync because another sync appears to be in progress.");
                return;
            }

            var syncService = scope.ServiceProvider.GetRequiredService<IHubSpotSyncService>();
            _logger.LogInformation("Triggering scheduled HubSpot sync.");

            var result = await syncService.RunIncrementalSyncAsync(cancellationToken);
            if (!result.Succeeded)
            {
                _logger.LogWarning("Scheduled HubSpot sync failed: {Message}", result.Message);
                return;
            }

            _logger.LogInformation(
                "Scheduled HubSpot sync completed. Fetched={Fetched}, Imported={Imported}, Updated={Updated}, Skipped={Skipped}.",
                result.DealsFetched,
                result.DealsImported,
                result.DealsUpdated,
                result.DealsSkipped);
        }
    }
}
