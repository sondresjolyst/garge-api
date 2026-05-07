namespace garge_api.Services
{
    public interface IOrderEmailService
    {
        Task SendOrderReservedAsync(int orderId);
    }
}
