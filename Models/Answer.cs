using System.ComponentModel.DataAnnotations;

namespace WebApplication2.Models
{
    public class Answer
    {
        public int Id { get; set; }
        
        [Required]
        public string Content { get; set; } = string.Empty;
        
        public int QuestionId { get; set; }
        
        // CHANGED: from int? to string? for Identity compatibility
        public string? UserId { get; set; }
        public DateTime CreatedDate { get; set; } = DateTime.Now;
        
        // Navigation Properties
        public Question Question { get; set; } = null!;
        
        [StringLength(255)]
        public string? AttachmentFileName { get; set; }

        [StringLength(500)]
        public string? AttachmentPath { get; set; }
    }
}