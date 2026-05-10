using garge_api.Helpers;
using garge_api.Models;
using garge_api.Models.Admin;
using garge_api.Models.Shop;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Web;

namespace garge_api.Services
{
    public class OrderEmailService : IOrderEmailService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IEmailService _emailService;
        private readonly ILogger<OrderEmailService> _logger;

        public OrderEmailService(
            IServiceScopeFactory scopeFactory,
            IEmailService emailService,
            ILogger<OrderEmailService> logger)
        {
            _scopeFactory = scopeFactory;
            _emailService = emailService;
            _logger = logger;
        }

        public async Task SendOrderConfirmedAsync(int orderId)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var order = await db.Orders
                .Include(o => o.User)
                .Include(o => o.OrderItems).ThenInclude(i => i.ShopItem)
                .FirstOrDefaultAsync(o => o.Id == orderId);

            if (order?.User?.Email == null)
            {
                _logger.LogWarning("Order {OrderId} missing user/email — confirmation email skipped", orderId);
                return;
            }

            var settings = await db.AppSettings.FindAsync(1) ?? new AppSettings();
            var html = BuildHtml(order, settings);

            try
            {
                await _emailService.SendEmailAsync(
                    order.User.Email,
                    $"Order #{order.Id} confirmed — {settings.CompanyName}",
                    html);
                _logger.LogInformation("Order confirmation email sent for order {OrderId}", orderId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send confirmation email for order {OrderId}", orderId);
            }
        }

        private static string BuildHtml(Order order, AppSettings s)
        {
            static string Nok(int ore) => MoneyFormat.Nok(ore);
            static string H(string? v) => HttpUtility.HtmlEncode(v ?? string.Empty);

            var lines = new StringBuilder();
            foreach (var item in order.OrderItems)
            {
                var lineTotal = item.PriceAtPurchaseInOre * item.Quantity;
                lines.Append($"""
                    <tr>
                      <td>{H(item.ShopItem?.Name)} × {item.Quantity}</td>
                      <td class="r">NOK {Nok(lineTotal)}</td>
                    </tr>
                    """);
            }

            var shipBlock = string.IsNullOrEmpty(order.ShippingAddress)
                ? string.Empty
                : $"""<p><strong>Ship to:</strong> {H(order.ShippingAddress)}</p>""";

            var body = $$"""
                <h1>Thanks for your order, {{H(order.User?.FirstName)}}!</h1>
                <p>We've received order <strong>#{{order.Id}}</strong> and will get it ready to ship.</p>

                <table>
                  <tbody>
                    {{lines}}
                  </tbody>
                </table>

                <div class="totals-section">
                  <table>
                    <tbody>
                      <tr class="grand">
                        <td>Total</td>
                        <td class="r">NOK {{Nok(order.TotalInOre)}}</td>
                      </tr>
                    </tbody>
                  </table>
                </div>

                {{shipBlock}}
                """;

            return EmailLayout.Render(s, new EmailLayout.Meta
            {
                Number = $"#{order.Id}",
                Subtitle = $"ORDER  ·  {order.CreatedAt:yyyy-MM-dd}"
            }, body);
        }
    }
}
