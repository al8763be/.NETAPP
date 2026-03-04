using Microsoft.AspNetCore.Identity;

namespace WebApplication2.Models
{
    public class HubSpotDealImport
    {
        public int Id { get; set; }
        public string ExternalDealId { get; set; } = string.Empty;
        public string? DealName { get; set; }
        public string? HubSpotOwnerId { get; set; }
        public HubSpotOwnerMapping? HubSpotOwner { get; set; }
        public string? SaljId { get; set; }
        public string OwnerEmail { get; set; } = string.Empty;
        public string? OwnerUserId { get; set; }
        public IdentityUser? OwnerUser { get; set; }
        public DateTime FulfilledDateUtc { get; set; }
        public decimal? Amount { get; set; }
        public decimal? SellerProvision { get; set; }
        public string? CurrencyCode { get; set; }
        public string? DealStage { get; set; }
        public DateTime? HubSpotLastModifiedUtc { get; set; }
        public string? PayloadHash { get; set; }
        public string? ContactFirstName { get; set; }
        public string? ContactPhoneNumber { get; set; }
        public string? ContactKundstatus { get; set; }
        public string? LineItemsJson { get; set; }
        public DateTime FirstSeenUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
    }
}
