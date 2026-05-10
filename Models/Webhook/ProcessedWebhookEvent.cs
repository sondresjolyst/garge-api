using System.ComponentModel.DataAnnotations;

namespace garge_api.Models.Webhook
{
    public class ProcessedWebhookEvent
    {
        [Key]
        [MaxLength(200)]
        public required string Id { get; set; }

        [Required]
        [MaxLength(50)]
        public required string Source { get; set; }

        public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;
    }
}
