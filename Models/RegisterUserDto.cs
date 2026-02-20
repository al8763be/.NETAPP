using System.ComponentModel.DataAnnotations;

namespace WebApplication2.Models
{
    public class RegisterUserDto
    {
        [Required]
        [StringLength(50)]
        public string EmployeeNumber { get; set; } = string.Empty;
        
        [Required]
        [StringLength(100, MinimumLength = 8)]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;
        
        [DataType(DataType.Password)]
        [Compare("Password", ErrorMessage = "Passwords don't match.")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}