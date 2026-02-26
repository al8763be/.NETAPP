using System.ComponentModel.DataAnnotations;

namespace WebApplication2.Services.HubSpot
{
    public class HubSpotOptions
    {
        [Required]
        public string BaseUrl { get; set; } = "https://api.hubapi.com";

        public bool Enabled { get; set; }

        // Must be provided via user-secrets or environment variable.
        public string? AccessToken { get; set; }

        [Required]
        public string FulfilledProperty { get; set; } = "dealstage";

        [Required]
        public string FulfilledValue { get; set; } = "closedwon";

        public List<string> FulfilledValues { get; set; } =
        [
            "nyregistrerad",
            "ombokning",
            "bokad",
            "klar kund",
            "installerad - ej fakturerad"
        ];

        [Required]
        public string DealNameProperty { get; set; } = "dealname";

        [Required]
        public string OwnerEmailProperty { get; set; } = "email";

        [Required]
        public string OwnerIdProperty { get; set; } = "hubspot_owner_id";

        [Required]
        public string SaljIdProperty { get; set; } = "saljid";

        [Required]
        public string FulfilledDateProperty { get; set; } = "forsaljningsdatum";

        [Required]
        public string ContactSaljareProperty { get; set; } = "saljare";

        [Required]
        public string DealFallbackDateProperty { get; set; } = "closedate";

        [Required]
        public string LastModifiedProperty { get; set; } = "hs_lastmodifieddate";

        [Required]
        public string AmountProperty { get; set; } = "amount";

        [Required]
        public string CurrencyCodeProperty { get; set; } = "deal_currency_code";

        [Required]
        public string ProvisionProperty { get; set; } = "saljarprovision";

        [Range(1, 100)]
        public int PageSize { get; set; } = 100;

        [Range(1, 100)]
        public int MaxPagesPerRun { get; set; } = 10;

        [Required]
        public string SyncCron { get; set; } = "0 */15 * * * *";

        [Required]
        public string UsernameEmailDomain { get; set; } = "stl.nu";
    }
}
