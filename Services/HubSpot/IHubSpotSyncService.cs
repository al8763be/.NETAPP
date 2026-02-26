namespace WebApplication2.Services.HubSpot
{
    public interface IHubSpotSyncService
    {
        Task<HubSpotSyncRunResult> RunIncrementalSyncAsync(CancellationToken cancellationToken = default);
        Task<HubSpotSyncRunResult> RebuildCurrentMonthOnlyAsync(CancellationToken cancellationToken = default);
    }

    public class HubSpotSyncRunResult
    {
        public bool Succeeded { get; set; }
        public int DealsFetched { get; set; }
        public int DealsImported { get; set; }
        public int DealsUpdated { get; set; }
        public int DealsSkipped { get; set; }
        public string? Message { get; set; }
    }
}
