using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace garge_api.Models.Shop
{
    public enum InvoiceKind
    {
        Invoice = 0,
        CreditNote = 1
    }

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

        /// <summary>The VAT rate in percent that applied when the sale was made. Zero before VAT registration.</summary>
        public int VatPercentage { get; set; }

        public InvoiceKind Kind { get; set; } = InvoiceKind.Invoice;

        /// <summary>On a credit note, the invoice it cancels.</summary>
        public int? CreditsInvoiceId { get; set; }

        /// <summary>On an invoice that replaces another, the invoice it replaces.</summary>
        public int? ReplacesInvoiceId { get; set; }

        /// <summary>
        /// For a sale made after the VAT threshold was passed, before registration: when the credit
        /// note and the new invoice with VAT were made, once VAT was on.
        /// </summary>
        public DateTime? VatCorrectedAt { get; set; }

        /// <summary>When the credit note and the new invoice were emailed to the customer.</summary>
        public DateTime? VatCorrectionEmailedAt { get; set; }

        /// <summary>
        /// When the PDF was last tried. A row with no PDF is a sale whose PDF failed or is being made,
        /// and the retry job makes it again after a while.
        /// </summary>
        public DateTime? PdfAttemptedAt { get; set; }

        public DateTime IssuedAt { get; set; } = DateTime.UtcNow;

        [Required]
        public required byte[] PdfData { get; set; }
    }
}
