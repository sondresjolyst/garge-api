using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Subscription
{
    public class SubscriptionResponseDto
    {
        public int Id { get; set; }
        public string UserId { get; set; } = string.Empty;
        public int ProductId { get; set; }
        public string ProductName { get; set; } = string.Empty;
        public int PriceInOre { get; set; }
        public string Interval { get; set; } = string.Empty;
        public string VippsAgreementId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTime? StartDate { get; set; }
        public DateTime? NextChargeDate { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class InitiateSubscriptionDto
    {
        [Required]
        public int ProductId { get; set; }

        [Required]
        public required string PhoneNumber { get; set; }

        [Required]
        public required string RedirectUrl { get; set; }
    }

    public class InitiateSubscriptionResponseDto
    {
        public int SubscriptionId { get; set; }
        public string VippsConfirmationUrl { get; set; } = string.Empty;
        public string VippsAgreementId { get; set; } = string.Empty;
    }

    public class VippsAgreementWebhookDto
    {
        public string AgreementId { get; set; } = string.Empty;
        public string EventType { get; set; } = string.Empty;
        public DateTime? Occurred { get; set; }
    }
}
