namespace WebApplication2.Services.HubSpot
{
    public interface IHubSpotClient
    {
        Task<HubSpotDealsPageResult> GetFulfilledDealsAsync(
            DateTime? modifiedSinceUtc,
            string? afterCursor,
            int pageSize,
            CancellationToken cancellationToken = default);

        Task<HubSpotDealsPageResult> SearchFulfilledDealsByClosedDateRangeAsync(
            DateTime closedDateStartUtc,
            DateTime closedDateEndUtc,
            string? afterCursor,
            int pageSize,
            CancellationToken cancellationToken = default);

        Task<HubSpotOwnerRecord?> GetOwnerByOwnerIdAsync(
            string ownerId,
            CancellationToken cancellationToken = default);
    }

    public class HubSpotDealRecord
    {
        public string ExternalDealId { get; set; } = string.Empty;
        public string? DealName { get; set; }
        public string? OwnerEmail { get; set; }
        public string? OwnerId { get; set; }
        public string? SaljId { get; set; }
        public List<string> ContactIds { get; set; } = new();
        public bool IsFulfilled { get; set; } = true;
        public DateTime? FulfilledDateUtc { get; set; }
        public DateTime? LastModifiedUtc { get; set; }
        public decimal? Amount { get; set; }
        public decimal? SellerProvision { get; set; }
        public string? CurrencyCode { get; set; }
        public string? DealStage { get; set; }
        public string? PayloadHash { get; set; }
    }

    public class HubSpotDealsPageResult
    {
        public List<HubSpotDealRecord> Deals { get; set; } = new();
        public string? NextCursor { get; set; }
    }

    public class HubSpotOwnerRecord
    {
        public string OwnerId { get; set; } = string.Empty;
        public string? Email { get; set; }
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public bool IsArchived { get; set; }
        public string? PrimaryTeamName { get; set; }
        public List<string> TeamNames { get; set; } = new();
    }
}
