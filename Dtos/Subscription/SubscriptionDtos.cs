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
        /// <summary>The unit price of a notified increase, or null when none is coming.</summary>
        public int? UpcomingPriceInOre { get; set; }
        /// <summary>The first charge due date that pays <see cref="UpcomingPriceInOre"/>.</summary>
        public DateTime? UpcomingPriceFrom { get; set; }
        public int Quantity { get; set; }
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

        [Range(1, 50)]
        public int Quantity { get; set; } = 1;
    }

    public class UpdateSubscriptionQuantityDto
    {
        [Required]
        [Range(1, 50)]
        public int Quantity { get; set; }
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
        public int Quantity { get; set; }
        public string Interval { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public bool IsTest { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? NextChargeDate { get; set; }
        public int InvoiceCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
