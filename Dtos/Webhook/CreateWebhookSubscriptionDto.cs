using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Webhook
{
    public class CreateWebhookSubscriptionDto
    {
        [Required]
        [MaxLength(500)]
        public string WebhookUrl { get; set; } = string.Empty;

        [MaxLength(256)]
        public string? WebhookSecret { get; set; }
    }
}
