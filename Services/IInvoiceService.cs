namespace garge_api.Services
{
    public interface IInvoiceService
    {
        Task<int> GenerateAndStoreAsync(int orderId);
    }
}
