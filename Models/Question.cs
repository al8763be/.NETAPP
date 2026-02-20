using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace WebApplication2.Models
{
    public class Question
    {
        public int Id { get; set; }
        
        [Required(ErrorMessage = "Kategori är obligatorisk")]
        [StringLength(20)]
        public string Category { get; set; } = "säljfråga"; // Default to säljfråga
        
        [Required(ErrorMessage = "Titel är obligatorisk")]
        [StringLength(200, MinimumLength = 5, ErrorMessage = "Titel måste vara mellan 5 och 200 tecken")]
        [RegularExpression(@"^[a-zA-Z0-9\s\-\.,\?\!åäöÅÄÖ]*$", ErrorMessage = "Titel innehåller ogiltiga tecken")]
        public string Title { get; set; } = string.Empty;
        
        [Required(ErrorMessage = "Innehåll är obligatoriskt")]
        [StringLength(5000, MinimumLength = 10, ErrorMessage = "Innehåll måste vara mellan 10 och 5000 tecken")]
        [DataType(DataType.MultilineText)]
        public string Content { get; set; } = string.Empty;
        
        // Identity uses string-based user IDs
        public string? UserId { get; set; }
        
        [Range(0, int.MaxValue, ErrorMessage = "Antal likes måste vara positivt")]
        public int Likes { get; set; } = 0;
        
        public bool IsFrequentlyAsked { get; set; } = false;
        
        public DateTime CreatedDate { get; set; } = DateTime.Now;
        
        // Navigation Properties
        public List<Answer> Answers { get; set; } = new List<Answer>();
        
        [StringLength(255)]
        [RegularExpression(@"^[a-zA-Z0-9\-_\.\s]*\.(pdf|doc|docx|png|jpg|jpeg)$", 
            ErrorMessage = "Endast giltiga filnamn med tillåtna filtyper")]
        public string? AttachmentFileName { get; set; }

        [StringLength(500)]
        public string? AttachmentPath { get; set; }

        // Helper method to get sanitized title for display
        public string GetSafeTitle()
        {
            return System.Net.WebUtility.HtmlEncode(Title);
        }

        // Helper method to get sanitized content for display
        public string GetSafeContent()
        {
            return System.Net.WebUtility.HtmlEncode(Content);
        }
    }

    // Custom validation attribute for safe content
    public class NoScriptTagsAttribute : ValidationAttribute
    {
        public override bool IsValid(object? value)
        {
            if (value is string input)
            {
                // Block script tags and other dangerous patterns
                var dangerousPatterns = new[]
                {
                    @"<script[^>]*>.*?</script>",
                    @"javascript:",
                    @"vbscript:",
                    @"onload=",
                    @"onerror=",
                    @"onclick=",
                    @"onmouseover="
                };

                foreach (var pattern in dangerousPatterns)
                {
                    if (Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        public override string FormatErrorMessage(string name)
        {
            return $"{name} innehåller potentiellt farligt innehåll";
        }
    }
}