using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Webhook
{
    public class WebhookSubscriptionDto
    {
        [Required]
        [MaxLength(500)]
        public string WebhookUrl { get; set; } = string.Empty;
        public int Id { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
