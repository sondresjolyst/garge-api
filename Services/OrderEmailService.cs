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
                      <td style="padding:6px 8px;border-bottom:1px solid #e5e7eb;">{H(item.ShopItem?.Name)} × {item.Quantity}</td>
                      <td style="padding:6px 8px;border-bottom:1px solid #e5e7eb;text-align:right;">NOK {Nok(lineTotal)}</td>
                    </tr>
                    """);
            }

            var shipBlock = string.IsNullOrEmpty(order.ShippingAddress)
                ? string.Empty
                : $"""<p style="margin:12px 0 0;"><strong>Ship to:</strong> {H(order.ShippingAddress)}</p>""";

            return $$"""
                <!DOCTYPE html>
                <html><body style="font-family:Arial,Helvetica,sans-serif;color:#1a1a1a;font-size:14px;">
                <div style="max-width:560px;margin:0 auto;padding:24px;">
                  <h1 style="font-size:20px;margin:0 0 8px;">Thanks for your order, {{H(order.User?.FirstName)}}!</h1>
                  <p style="color:#555;margin:0 0 16px;">We've received order <strong>#{{order.Id}}</strong> and will get it ready to ship.</p>
                  <table style="width:100%;border-collapse:collapse;margin:16px 0;">{{lines}}
                    <tr><td style="padding:8px;font-weight:700;">Total</td><td style="padding:8px;text-align:right;font-weight:700;">NOK {{Nok(order.TotalInOre)}}</td></tr>
                  </table>
                  {{shipBlock}}
                  <p style="color:#888;font-size:12px;margin-top:24px;">{{H(s.CompanyLegalName)}} · Org. no. {{H(s.CompanyOrgNumber)}}</p>
                </div>
                </body></html>
                """;
        }
    }
}
