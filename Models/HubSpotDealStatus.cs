namespace WebApplication2.Models
{
    public static class HubSpotDealStatus
    {
        public static bool IsLostKundstatus(string? kundstatus)
            => GetNormalizedStatus(kundstatus) is "annullerat" or "winback" or "saljare" or "säljare";

        public static bool ExcludesFulfilledStatus(string? kundstatus)
            => GetNormalizedStatus(kundstatus) is "avslag" || IsLostKundstatus(kundstatus);

        public static string GetNormalizedStatus(string? kundstatus)
            => kundstatus?.Trim().ToLowerInvariant() ?? string.Empty;
    }
}
