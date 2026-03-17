namespace WebApplication2.Models
{
    public class UserProfileViewModel
    {
        public const int CurrentMonthOffset = 0;
        public const int PreviousMonthOffset = -1;

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
        public int SelectedMonthOffset { get; set; }
        public string SelectedPeriodLabel { get; set; } = string.Empty;
        public bool IsCurrentMonthSelected => SelectedMonthOffset == CurrentMonthOffset;
        public bool IsPreviousMonthSelected => SelectedMonthOffset == PreviousMonthOffset;

        public int SelectedPeriodFulfilledDealsCount { get; set; }
        public decimal SelectedPeriodFulfilledDealsAmount { get; set; }
        public decimal SelectedPeriodFulfilledDealsProvision { get; set; }
        public List<UserHubSpotDealViewModel> SelectedPeriodDeals { get; set; } = new();
        public List<UserHubSpotDealViewModel> SelectedPeriodLostDeals { get; set; } = new();
    }

    public class UserHubSpotDealViewModel
    {
        public string ExternalDealId { get; set; } = string.Empty;
        public string DealName { get; set; } = string.Empty;
        public DateTime FulfilledDateUtc { get; set; }
        public decimal? Amount { get; set; }
        public decimal? SellerProvision { get; set; }
        public string CurrencyCode { get; set; } = string.Empty;
        public string ContactFirstName { get; set; } = string.Empty;
        public string ContactPhoneNumber { get; set; } = string.Empty;
        public string ContactKundstatus { get; set; } = string.Empty;
        public List<UserHubSpotDealLineItemViewModel> LineItems { get; set; } = new();
    }

    public class UserHubSpotDealLineItemViewModel
    {
        public string LineItemId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public decimal? Quantity { get; set; }
        public decimal? Price { get; set; }
        public decimal? Amount { get; set; }
        public string Sku { get; set; } = string.Empty;
    }

    public class UserHubSpotDealTableViewModel
    {
        public string DetailRowPrefix { get; set; } = string.Empty;
        public string EmptyMessage { get; set; } = string.Empty;
        public List<UserHubSpotDealViewModel> Deals { get; set; } = new();
    }
}
