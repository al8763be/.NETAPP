using Microsoft.AspNetCore.Identity;

namespace WebApplication2.Models
{
    public class HubSpotOwnerMapping
    {
        public int Id { get; set; }
        public string HubSpotOwnerId { get; set; } = string.Empty;
        public string? HubSpotOwnerEmail { get; set; }
        public string? HubSpotFirstName { get; set; }
        public string? HubSpotLastName { get; set; }
        public string? HubSpotPrimaryTeamName { get; set; }
        public string? HubSpotTeamNames { get; set; }
        public bool IsArchived { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public DateTime? LastOwnerSyncUtc { get; set; }

        // Local app mapping (employee login identity)
        public string? OwnerUserId { get; set; }
        public IdentityUser? OwnerUser { get; set; }
        public string? OwnerUsername { get; set; }

        public ICollection<HubSpotDealImport> FulfilledDeals { get; set; } = new List<HubSpotDealImport>();
    }
}
