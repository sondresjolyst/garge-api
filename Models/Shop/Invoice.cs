using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace garge_api.Models.Shop
{
    public class Invoice
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        // Exactly one of OrderId / SubscriptionId is set. Order = one-off shop purchase,
        // Subscription = a single recurring charge against a Vipps agreement.
        public int? OrderId { get; set; }

        [ForeignKey(nameof(OrderId))]
        public Order? Order { get; set; }

        public int? SubscriptionId { get; set; }

        [ForeignKey(nameof(SubscriptionId))]
        public Subscription.Subscription? Subscription { get; set; }

        // Vipps charge id for the subscription path. Lets us guard against webhook
        // redelivery so a single charge never produces two invoices.
        [MaxLength(200)]
        public string? VippsChargeId { get; set; }

        // Snapshot of the amount charged in øre. For order invoices we pull this from the
        // Order; for subscription charges we capture it at invoice time so future product
        // price changes don't rewrite history.
        public int AmountInOre { get; set; }

        public DateTime IssuedAt { get; set; } = DateTime.UtcNow;

        [Required]
        public required byte[] PdfData { get; set; }
    }
}
