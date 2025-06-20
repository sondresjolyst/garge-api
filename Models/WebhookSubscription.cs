using System.ComponentModel.DataAnnotations;

namespace garge_api.Models
{
    public class WebhookSubscription
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [MaxLength(500)]
        public string WebhookUrl { get; set; } = string.Empty;

        [Required]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
