using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace WebApplication2.Services.HubSpot
{
    public class HubSpotMappingService : IHubSpotMappingService
    {
        private static readonly Regex EmployeeUsernamePattern = new(@"^\d{4}$", RegexOptions.Compiled);
        private readonly HubSpotOptions _options;

        public HubSpotMappingService(IOptions<HubSpotOptions> options)
        {
            _options = options.Value;
        }

        public bool IsValidEmployeeUsername(string username)
        {
            return !string.IsNullOrWhiteSpace(username) && EmployeeUsernamePattern.IsMatch(username.Trim());
        }

        public bool TryBuildEmployeeEmail(string username, out string email)
        {
            email = string.Empty;
            if (!IsValidEmployeeUsername(username))
            {
                return false;
            }

            var domain = _options.UsernameEmailDomain.Trim().TrimStart('@').ToLowerInvariant();
            email = $"{username.Trim()}@{domain}";
            return true;
        }

        public bool TryExtractEmployeeUsername(string email, out string username)
        {
            username = string.Empty;
            if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            {
                return false;
            }

            var parts = email.Trim().Split('@', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
            {
                return false;
            }

            if (!IsValidEmployeeUsername(parts[0]))
            {
                return false;
            }

            var domain = _options.UsernameEmailDomain.Trim().TrimStart('@');
            if (!parts[1].Equals(domain, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            username = parts[0];
            return true;
        }
    }
}
