using System.ComponentModel.DataAnnotations;

namespace WebApplication2.Models
{
    public class Like
    {
        public int Id { get; set; }
        public int QuestionId { get; set; }
        
        // CHANGED: from int? to string? for Identity compatibility
        public string? UserId { get; set; }
        
        [Required]
        [StringLength(50)]
        public string OriginalUsername { get; set; } = string.Empty;
        
        public DateTime CreatedDate { get; set; } = DateTime.Now;
        
        // Navigation Properties
        public Question Question { get; set; } = null!;
    }
}