using Microsoft.AspNetCore.Identity;

namespace WebApplication2.Models
{
    public class EmployeeProfile
    {
        public int Id { get; set; }
        public string EmployeeNumber { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string Region { get; set; } = string.Empty;
        public string? Role { get; set; }
        public string? UserId { get; set; }
        public IdentityUser? User { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }
}
