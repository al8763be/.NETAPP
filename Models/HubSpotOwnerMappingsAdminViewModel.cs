namespace WebApplication2.Models
{
    public class HubSpotOwnerMappingsAdminViewModel
    {
        public string SearchTerm { get; set; } = string.Empty;
        public bool ShowUnmappedOnly { get; set; }
        public List<HubSpotOwnerMappingRowViewModel> OwnerMappings { get; set; } = new();
        public List<HubSpotUserOptionViewModel> UserOptions { get; set; } = new();
    }

    public class HubSpotOwnerMappingRowViewModel
    {
        public string HubSpotOwnerId { get; set; } = string.Empty;
        public string? HubSpotOwnerEmail { get; set; }
        public string? HubSpotFirstName { get; set; }
        public string? HubSpotLastName { get; set; }
        public bool IsArchived { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public DateTime? LastOwnerSyncUtc { get; set; }
        public string? OwnerUserId { get; set; }
        public string? OwnerUsername { get; set; }
        public bool IsMapped => !string.IsNullOrWhiteSpace(OwnerUserId);
        public string DisplayName => $"{HubSpotFirstName} {HubSpotLastName}".Trim();
    }

    public class HubSpotUserOptionViewModel
    {
        public string Id { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string? Email { get; set; }
    }
}
