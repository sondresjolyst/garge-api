namespace garge_api.Services
{
    public interface IInvoiceService
    {
        /// <summary>
        /// For each sale that owes VAT from before registration, makes a credit note for its VAT-free
        /// invoice and a new invoice with VAT included in the same price, and emails both to the
        /// customer. This is Skatteetaten's credit note and new invoice method. Returns how many sales
        /// were corrected. Only runs with VAT on.
        /// </summary>
        Task<int> GenerateVatCorrectionsAsync(CancellationToken ct = default);

        /// <summary>
        /// Makes a credit note for what has been paid back on a sale and not credited yet, against the
        /// sale's current invoice: the replacement after a VAT correction, otherwise the original. Emails
        /// it to the customer. Returns the credit note's id, or null when nothing was left to credit.
        /// </summary>
        Task<int?> CreditRefundsAsync(int saleInvoiceId, CancellationToken ct = default);

        /// <summary>Credits the refunds of a shop order, once its invoice exists.</summary>
        Task<int?> CreditOrderRefundsAsync(int orderId, CancellationToken ct = default);

        /// <summary>
        /// Makes the PDFs that failed or were never made, and sends the invoice and VAT correction
        /// emails Brevo did not accept. Returns how many PDFs and corrections were made.
        /// </summary>
        Task<int> RetryMissingPdfsAsync(CancellationToken ct = default);

        /// <summary>
        /// Generate the invoice for an order and email the buyer. Idempotent by default: if an
        /// invoice already exists for the order, the call is a no-op and returns the existing id.
        /// Pass <paramref name="force"/> = true to re-render the PDF and re-send the email,
        /// reusing the same invoice row.
        /// </summary>
        Task<int> GenerateAndStoreAsync(int orderId, bool force = false);

        /// <summary>
        /// Generate the invoice for a single recurring charge against a Vipps subscription
        /// agreement and email the buyer. Idempotent on <paramref name="vippsChargeId"/>:
        /// webhook redelivery for the same charge returns the existing invoice id without
        /// inserting a duplicate row or re-emailing.
        /// </summary>
        Task<int> GenerateForSubscriptionChargeAsync(
            int subscriptionId, string vippsChargeId, int amountInOre, DateTime occurredAt);
    }
}
