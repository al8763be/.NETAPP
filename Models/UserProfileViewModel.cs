namespace WebApplication2.Models
{
    public class UserProfileViewModel
    {
        public string UserId { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public bool EmailConfirmed { get; set; }
        public bool TwoFactorEnabled { get; set; }
        public List<string> Roles { get; set; } = new();
        public int QuestionsCount { get; set; }
        public int AnswersCount { get; set; }
        public int LikesGivenCount { get; set; }
        public int ContestEntriesCount { get; set; }

        public string? HubSpotOwnerId { get; set; }
        public string? HubSpotOwnerEmail { get; set; }
        public string? HubSpotOwnerDisplayName { get; set; }
        public bool? HubSpotOwnerArchived { get; set; }
        public bool HasHubSpotOwnerMapping => !string.IsNullOrWhiteSpace(HubSpotOwnerId);

        public int CurrentMonthFulfilledDealsCount { get; set; }
        public decimal CurrentMonthFulfilledDealsAmount { get; set; }
        public decimal CurrentMonthFulfilledDealsProvision { get; set; }
        public List<UserHubSpotDealViewModel> CurrentMonthDeals { get; set; } = new();
    }

    public class UserHubSpotDealViewModel
    {
        public string ExternalDealId { get; set; } = string.Empty;
        public string DealName { get; set; } = string.Empty;
        public DateTime FulfilledDateUtc { get; set; }
        public decimal? Amount { get; set; }
        public decimal? SellerProvision { get; set; }
        public string CurrencyCode { get; set; } = string.Empty;
    }
}
