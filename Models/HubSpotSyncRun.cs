namespace WebApplication2.Models
{
    public class HubSpotSyncRun
    {
        public long Id { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }
        public string Status { get; set; } = "Started";
        public int DealsFetched { get; set; }
        public int DealsImported { get; set; }
        public int DealsUpdated { get; set; }
        public int DealsSkipped { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
