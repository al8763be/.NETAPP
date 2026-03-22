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

        Task EnrichDealsWithLineItemsAsync(
            List<HubSpotDealRecord> deals,
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
        public string? ContactFirstName { get; set; }
        public string? ContactPhoneNumber { get; set; }
        public string? ContactKundstatus { get; set; }
        public List<string> LineItemIds { get; set; } = new();
        public List<HubSpotDealLineItemRecord> LineItems { get; set; } = new();
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

    public class HubSpotDealLineItemRecord
    {
        public string LineItemId { get; set; } = string.Empty;
        public string? Name { get; set; }
        public decimal? Quantity { get; set; }
        public decimal? Price { get; set; }
        public decimal? Amount { get; set; }
        public string? Sku { get; set; }
    }
}
