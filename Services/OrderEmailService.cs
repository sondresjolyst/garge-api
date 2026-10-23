using garge_api.Helpers;
using garge_api.Models;
using garge_api.Models.Admin;
using garge_api.Models.Shop;
using Microsoft.EntityFrameworkCore;
using SjolystInnovation.AppKit.Pdf;
using System.Text;
using System.Web;

namespace garge_api.Services
{
    public class OrderEmailService : IOrderEmailService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IEmailService _emailService;
        private readonly IPdfRenderer _pdfRenderer;
        private readonly ILogger<OrderEmailService> _logger;

        public OrderEmailService(
            IServiceScopeFactory scopeFactory,
            IEmailService emailService,
            IPdfRenderer pdfRenderer,
            ILogger<OrderEmailService> logger)
        {
            _scopeFactory = scopeFactory;
            _emailService = emailService;
            _pdfRenderer = pdfRenderer;
            _logger = logger;
        }

        public async Task SendOrderConfirmedAsync(int orderId, CancellationToken ct = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var order = await db.Orders
                .Include(o => o.User)
                .Include(o => o.OrderItems).ThenInclude(i => i.ShopItem)
                .FirstOrDefaultAsync(o => o.Id == orderId, ct);

            if (order?.User?.Email == null)
            {
                _logger.LogWarning("Order {OrderId} missing user/email — confirmation email skipped", orderId);
                return;
            }

            var settings = await db.AppSettings.FindAsync(1) ?? new AppSettings();
            var attachments = await WithdrawalFormAsync(order, settings, ct);
            var html = BuildHtml(order, settings, attachments.Count > 0);

            try
            {
                await _emailService.SendEmailAsync(
                    order.User.Email,
                    $"Order #{order.Id} confirmed — {settings.CompanyName}",
                    html,
                    attachments);
                _logger.LogInformation("Order confirmation email sent for order {OrderId}", orderId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send confirmation email for order {OrderId}", orderId);
            }
        }

        // The confirmation goes out even when the form fails to render, so the buyer always hears about the order.
        private async Task<IReadOnlyList<EmailAttachment>> WithdrawalFormAsync(Order order, AppSettings settings, CancellationToken ct)
        {
            try
            {
                var pdf = await _pdfRenderer.RenderAsync(WithdrawalForm.Html(BuildWithdrawalForm(order, settings)), ct);
                return
                [
                    new EmailAttachment
                    {
                        FileName = WithdrawalForm.FileName(WithdrawalFormLanguage.English),
                        Content = pdf,
                        ContentType = "application/pdf"
                    }
                ];
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Withdrawal form for order {OrderId} failed to render, the confirmation is sent without it", order.Id);
                return [];
            }
        }

        /// <summary>The English withdrawal form, filled in with the seller from the site settings and the order's items and date.</summary>
        internal static WithdrawalFormRequest BuildWithdrawalForm(Order order, AppSettings s) => new(
            new WithdrawalFormSeller(s.CompanyName)
            {
                LegalName = s.CompanyLegalName,
                OrganizationNumber = s.OrgNumber,
                AddressLines = [s.FormattedAddress],
                Email = s.PublicEmail
            },
            new WithdrawalFormOrder(
                $"#{order.Id}",
                DateOnly.FromDateTime(LocalTime.Date(order.CreatedAt)),
                order.OrderItems.Select(WithdrawalFormItem).ToList()))
        {
            // garge's emails and legal pages are in English.
            Language = WithdrawalFormLanguage.English
        };

        // The quantity leads the name like the invoice's goods lines, and a single item is just its name.
        private static string WithdrawalFormItem(OrderItem item)
        {
            var name = ItemName(item);
            return item.Quantity > 1 ? $"{item.Quantity} × {name}" : name;
        }

        // An order line stores no name of its own, so a deleted shop item falls back to its id rather than a blank line.
        private static string ItemName(OrderItem item) =>
            string.IsNullOrWhiteSpace(item.ShopItem?.Name) ? $"Item #{item.ShopItemId}" : item.ShopItem.Name;

        private static string BuildHtml(Order order, AppSettings s, bool withdrawalFormAttached)
        {
            static string Nok(int ore) => MoneyFormat.Nok(ore);
            static string H(string? v) => HttpUtility.HtmlEncode(v ?? string.Empty);

            var lines = new StringBuilder();
            foreach (var item in order.OrderItems)
            {
                var lineTotal = item.PriceAtPurchaseInOre * item.Quantity;
                lines.Append($"""
                    <tr>
                      <td>{H(ItemName(item))} × {item.Quantity}</td>
                      <td class="r">NOK {Nok(lineTotal)}</td>
                    </tr>
                    """);
            }

            var shipBlock = string.IsNullOrEmpty(order.ShippingAddress)
                ? string.Empty
                : $"""<p><strong>Ship to:</strong> {H(order.ShippingAddress)}</p>""";

            var withdrawalBlock = withdrawalFormAttached
                ? "<p>The withdrawal form is attached if you want to cancel within 14 days of receiving your order.</p>"
                : string.Empty;

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

                {{withdrawalBlock}}
                """;

            return EmailLayout.Render(s, new EmailLayout.Meta
            {
                Number = $"#{order.Id}",
                Subtitle = $"ORDER  ·  {LocalTime.Date(order.CreatedAt):yyyy-MM-dd}"
            }, body);
        }
    }
}
