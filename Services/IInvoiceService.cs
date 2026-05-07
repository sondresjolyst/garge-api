namespace garge_api.Services
{
    public interface IInvoiceService
    {
        /// <summary>
        /// Generate the invoice for an order and email the buyer. Idempotent by default: if an
        /// invoice already exists for the order, the call is a no-op and returns the existing id.
        /// Pass <paramref name="force"/> = true to re-render the PDF and re-send the email,
        /// reusing the same invoice row.
        /// </summary>
        Task<int> GenerateAndStoreAsync(int orderId, bool force = false);
    }
}
