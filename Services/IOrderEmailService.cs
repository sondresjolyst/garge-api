namespace garge_api.Services
{
    public interface IOrderEmailService
    {
        Task SendOrderConfirmedAsync(int orderId);
    }
}
