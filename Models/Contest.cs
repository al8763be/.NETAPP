using System.ComponentModel.DataAnnotations;

namespace WebApplication2.Models
{
    public class Contest
    {
        public int Id { get; set; }
        
        [Required]
        [StringLength(100)]
        public string Name { get; set; } = string.Empty;
        
        public string? Description { get; set; }
        
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        
        // CHANGED: from int? to string? for Identity compatibility
        public string? LeaderUserId { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedDate { get; set; } = DateTime.Now;
    }
}