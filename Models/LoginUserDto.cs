using System.ComponentModel.DataAnnotations;

namespace WebApplication2.Models
{
    public class LoginUserDto
    {
        [Required]
        [StringLength(50)]
        public string EmployeeNumber { get; set; } = string.Empty;
        
        [Required]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;
        
        public bool RememberMe { get; set; } = false;
    }
}