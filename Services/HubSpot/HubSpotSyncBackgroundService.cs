using Microsoft.EntityFrameworkCore;
using WebApplication2.Data;

namespace WebApplication2.Services.HubSpot
{
    public class HubSpotSyncBackgroundService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly HubSpotOptions _options;
        private readonly ILogger<HubSpotSyncBackgroundService> _logger;

        public HubSpotSyncBackgroundService(
            IServiceScopeFactory scopeFactory,
            Microsoft.Extensions.Options.IOptions<HubSpotOptions> options,
            ILogger<HubSpotSyncBackgroundService> logger)
        {
            _scopeFactory = scopeFactory;
            _options = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var syncInterval = ResolveSyncInterval(_options.SyncCron);
            _logger.LogInformation(
                "HubSpot background sync service started. Scheduled sync runs every {IntervalMinutes} minutes (SyncCron: {SyncCron}).",
                (int)syncInterval.TotalMinutes,
                _options.SyncCron);

            while (!stoppingToken.IsCancellationRequested)
            {
                var nowUtc = DateTime.UtcNow;
                var nextRunUtc = GetNextRunUtc(nowUtc, syncInterval);

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
                    r => r.Status == "Started" && r.StartedUtc >= nowUtc.AddMinutes(-30),
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

        private static TimeSpan ResolveSyncInterval(string? syncCron)
        {
            const int fallbackMinutes = 5;

            if (string.IsNullOrWhiteSpace(syncCron))
            {
                return TimeSpan.FromMinutes(fallbackMinutes);
            }

            var parts = syncCron
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (parts.Length == 6 &&
                parts[0] == "0" &&
                parts[1].StartsWith("*/", StringComparison.Ordinal) &&
                int.TryParse(parts[1][2..], out var intervalMinutes) &&
                intervalMinutes > 0 &&
                intervalMinutes <= 59)
            {
                return TimeSpan.FromMinutes(intervalMinutes);
            }

            return TimeSpan.FromMinutes(fallbackMinutes);
        }

        private static DateTime GetNextRunUtc(DateTime nowUtc, TimeSpan syncInterval)
        {
            var intervalMinutes = Math.Max(1, (int)syncInterval.TotalMinutes);
            var currentMinuteBucket = nowUtc.Minute / intervalMinutes;
            var currentBoundaryUtc = new DateTime(
                nowUtc.Year,
                nowUtc.Month,
                nowUtc.Day,
                nowUtc.Hour,
                currentMinuteBucket * intervalMinutes,
                0,
                DateTimeKind.Utc);

            var nextRunUtc = currentBoundaryUtc.AddMinutes(intervalMinutes);
            if (nextRunUtc <= nowUtc)
            {
                nextRunUtc = nowUtc.AddMinutes(intervalMinutes);
            }

            return nextRunUtc;
        }
    }
}
