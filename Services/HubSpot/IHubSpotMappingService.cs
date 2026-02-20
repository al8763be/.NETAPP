namespace WebApplication2.Services.HubSpot
{
    public interface IHubSpotMappingService
    {
        bool IsValidEmployeeUsername(string username);
        bool TryBuildEmployeeEmail(string username, out string email);
        bool TryExtractEmployeeUsername(string email, out string username);
    }
}
