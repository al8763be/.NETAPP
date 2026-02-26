using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WebApplication2.Models
{
    public class ContestEntry
    {
        public int Id { get; set; }
        
        public int ContestId { get; set; }
        
        // CHANGED: from int? to string? for Identity compatibility
        public string? UserId { get; set; }
        
        [Required]
        [StringLength(50)]
        public string EmployeeNumber { get; set; } = string.Empty;
        
        public int DealsCount { get; set; } = 0;
        public DateTime UpdatedDate { get; set; } = DateTime.Now;

        [NotMapped]
        public string? DisplayName { get; set; }
        
        // Navigation Properties
        public Contest Contest { get; set; } = null!;
    }
}
