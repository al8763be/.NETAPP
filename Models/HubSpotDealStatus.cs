namespace WebApplication2.Models
{
    public static class HubSpotDealStatus
    {
        public static bool IsFulfilledKundstatus(string? kundstatus)
            => GetNormalizedStatus(kundstatus) is
                "nyregistrerad" or
                "ombokning" or
                "bokad" or
                "klar kund" or
                "installerad - ej fakturerad";

        public static bool IsLostKundstatus(string? kundstatus)
            => GetNormalizedStatus(kundstatus) is "annullerat" or "winback" or "saljare" or "säljare";

        public static string GetNormalizedStatus(string? kundstatus)
            => kundstatus?.Trim().ToLowerInvariant() ?? string.Empty;
    }
}
