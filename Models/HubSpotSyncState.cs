namespace WebApplication2.Models
{
    public class HubSpotSyncState
    {
        public int Id { get; set; }
        public string IntegrationName { get; set; } = "HubSpotDeals";
        public DateTime? LastSuccessfulSyncUtc { get; set; }
        public string? LastCursor { get; set; }
        public DateTime? LastAttemptUtc { get; set; }
        public string? LastError { get; set; }
    }
}
