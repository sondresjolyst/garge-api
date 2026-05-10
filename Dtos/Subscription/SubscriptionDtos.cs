using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Subscription
{
    public class SubscriptionResponseDto
    {
        public int Id { get; set; }
        public string UserId { get; set; } = string.Empty;
        public int ProductId { get; set; }
        public string ProductName { get; set; } = string.Empty;
        public string ProductType { get; set; } = string.Empty;
        public int PriceInOre { get; set; }
        public string Interval { get; set; } = string.Empty;
        public string VippsAgreementId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public bool IsTest { get; set; }
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
        [RegularExpression(@"^(?:47)?\d{8}$", ErrorMessage = "Phone number must be 8 Norwegian digits, optionally prefixed with 47.")]
        public required string PhoneNumber { get; set; }

        [Required]
        public bool ConsentToWaiveWithdrawal { get; set; }
    }

    public class InitiateSubscriptionResponseDto
    {
        public int SubscriptionId { get; set; }
        public string VippsConfirmationUrl { get; set; } = string.Empty;
        public string VippsAgreementId { get; set; } = string.Empty;
    }

    public class AdminSubscriptionResponseDto
    {
        public int Id { get; set; }
        public string UserId { get; set; } = string.Empty;
        public string UserEmail { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string ProductType { get; set; } = string.Empty;
        public int PriceInOre { get; set; }
        public string Interval { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public bool IsTest { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? NextChargeDate { get; set; }
        public int InvoiceCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class VippsAgreementWebhookDto
    {
        public string AgreementId { get; set; } = string.Empty;
        public string EventId { get; set; } = string.Empty;
        public string EventType { get; set; } = string.Empty;
        public DateTime? Occurred { get; set; }
        public string? Msn { get; set; }

        // Set on recurring.charge-* events. Used as the idempotency key when
        // generating subscription invoices so a redelivered webhook can't
        // produce two invoices for the same charge.
        public string? ChargeId { get; set; }
    }
}
