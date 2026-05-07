using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace garge_api.Models.Subscription
{
    public enum SubscriptionStatus
    {
        Pending = 0,
        Active = 1,
        Stopped = 2,
        Expired = 3
    }

    public class Subscription
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        [Required]
        public required string UserId { get; set; }

        [ForeignKey(nameof(UserId))]
        public User? User { get; set; }

        [Required]
        public int ProductId { get; set; }

        [ForeignKey(nameof(ProductId))]
        public Product? Product { get; set; }

        [Required]
        [MaxLength(200)]
        public required string VippsAgreementId { get; set; }

        [Column(TypeName = "text")]
        public string? VippsConfirmationUrl { get; set; }

        [Required]
        public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Pending;

        public DateTime? StartDate { get; set; }

        public DateTime? NextChargeDate { get; set; }

        public DateTime? ConsentAcceptedAt { get; set; }

        [MaxLength(45)]
        public string? ConsentIp { get; set; }

        public bool IsTest { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
