using garge_api.Helpers;
using garge_api.Models;
using garge_api.Models.Admin;
using garge_api.Models.Shop;
using garge_api.Models.Subscription;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text;
using System.Web;

namespace garge_api.Services
{
    public class InvoiceService : IInvoiceService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IEmailService _emailService;
        private readonly IPdfRenderer _pdfRenderer;
        private readonly ILogger<InvoiceService> _logger;

        public InvoiceService(
            IServiceScopeFactory scopeFactory,
            IEmailService emailService,
            IPdfRenderer pdfRenderer,
            ILogger<InvoiceService> logger)
        {
            _scopeFactory = scopeFactory;
            _emailService = emailService;
            _pdfRenderer = pdfRenderer;
            _logger = logger;
        }

        public async Task<int> GenerateAndStoreAsync(int orderId, bool force = false)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var order = await db.Orders
                .Include(o => o.User)
                .Include(o => o.OrderItems).ThenInclude(i => i.ShopItem)
                .FirstOrDefaultAsync(o => o.Id == orderId)
                ?? throw new InvalidOperationException($"Order {orderId} not found");

            var settings = await db.AppSettings.FindAsync(1) ?? new AppSettings();

            var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.OrderId == orderId);
            if (invoice != null && !force)
            {
                _logger.LogInformation("Invoice {InvoiceId} already exists or is in progress for order {OrderId} — skip", invoice.Id, orderId);
                return invoice.Id;
            }

            var wasNewRow = invoice == null;
            if (invoice == null)
            {
                invoice = new Invoice { OrderId = orderId, IssuedAt = DateTime.UtcNow, PdfData = [] };
                db.Invoices.Add(invoice);
                await db.SaveChangesAsync();
            }
            else
            {
                invoice.IssuedAt = DateTime.UtcNow;
            }

            var html = BuildInvoiceHtml(order, settings, invoice.Id, invoice.IssuedAt);
            try
            {
                invoice.PdfData = await _pdfRenderer.RenderAsync(html);
                await db.SaveChangesAsync();
            }
            catch
            {
                // Keep DB clean: drop the row we just added so retry isn't blocked by an
                // empty-PDF placeholder. For force-regenerate over an existing complete
                // invoice we leave the prior row + bytes alone.
                if (wasNewRow || invoice.PdfData.Length == 0)
                {
                    db.Invoices.Remove(invoice);
                    await db.SaveChangesAsync();
                }
                throw;
            }

            try
            {
                var buyerEmail = order.User?.Email;
                if (!string.IsNullOrEmpty(buyerEmail))
                {
                    var attachment = new EmailAttachment
                    {
                        FileName = $"invoice-{invoice.Id:D4}.pdf",
                        Content = invoice.PdfData,
                        ContentType = "application/pdf"
                    };
                    await _emailService.SendEmailAsync(
                        buyerEmail,
                        $"Invoice #{invoice.Id:D4} — {settings.CompanyName}",
                        html,
                        [attachment]);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to email invoice {InvoiceId} for order {OrderId}", invoice.Id, orderId);
            }

            _logger.LogInformation("Invoice {InvoiceId} generated for order {OrderId}", invoice.Id, orderId);
            return invoice.Id;
        }

        public async Task<int> GenerateForSubscriptionChargeAsync(
            int subscriptionId, string vippsChargeId, int amountInOre, DateTime occurredAt)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var existing = await db.Invoices.FirstOrDefaultAsync(i => i.VippsChargeId == vippsChargeId);
            if (existing != null)
            {
                _logger.LogInformation("Invoice {InvoiceId} already exists for charge {ChargeId} — skip",
                    existing.Id, vippsChargeId);
                return existing.Id;
            }

            var subscription = await db.Subscriptions
                .Include(s => s.User)
                .Include(s => s.Product)
                .FirstOrDefaultAsync(s => s.Id == subscriptionId)
                ?? throw new InvalidOperationException($"Subscription {subscriptionId} not found");

            var settings = await db.AppSettings.FindAsync(1) ?? new AppSettings();

            var invoice = new Invoice
            {
                SubscriptionId = subscription.Id,
                VippsChargeId = vippsChargeId,
                AmountInOre = amountInOre,
                IssuedAt = occurredAt,
                PdfData = []
            };
            db.Invoices.Add(invoice);
            await db.SaveChangesAsync();

            var html = BuildSubscriptionInvoiceHtml(subscription, settings, invoice.Id, occurredAt, amountInOre);
            try
            {
                invoice.PdfData = await _pdfRenderer.RenderAsync(html);
                await db.SaveChangesAsync();
            }
            catch
            {
                db.Invoices.Remove(invoice);
                await db.SaveChangesAsync();
                throw;
            }

            try
            {
                var buyerEmail = subscription.User?.Email;
                if (!string.IsNullOrEmpty(buyerEmail))
                {
                    var attachment = new EmailAttachment
                    {
                        FileName = $"invoice-{invoice.Id:D4}.pdf",
                        Content = invoice.PdfData,
                        ContentType = "application/pdf"
                    };
                    await _emailService.SendEmailAsync(
                        buyerEmail,
                        $"Invoice #{invoice.Id:D4} — {settings.CompanyName}",
                        html,
                        [attachment]);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to email invoice {InvoiceId} for subscription {SubscriptionId} charge {ChargeId}",
                    invoice.Id, subscriptionId, vippsChargeId);
            }

            _logger.LogInformation("Invoice {InvoiceId} generated for subscription {SubscriptionId} charge {ChargeId}",
                invoice.Id, subscriptionId, vippsChargeId);
            return invoice.Id;
        }

        private static string BuildSubscriptionInvoiceHtml(
            Subscription subscription, AppSettings s, int invoiceId, DateTime issuedAt, int amountInOre)
        {
            static string Nok(int ore) => MoneyFormat.Nok(ore);
            static string H(string? v) => HttpUtility.HtmlEncode(v ?? string.Empty);

            var product = subscription.Product;
            var productName = product?.Name ?? "Subscription";
            var period = product?.Interval == BillingInterval.Yearly ? "year" : "month";
            var buyerName = subscription.User != null
                ? $"{subscription.User.FirstName} {subscription.User.LastName}"
                : "—";

            int net = amountInOre, vatAmount = 0;
            if (s.VatEnabled)
            {
                net = (int)Math.Round(amountInOre / 1.25);
                vatAmount = amountInOre - net;
            }
            var subRows = BuildVatSubRows(s.VatEnabled, net, vatAmount);

            var partiesHtml = EmailLayout.RenderParties(
                from: new EmailLayout.Party
                {
                    Label = "From",
                    Name = s.CompanyLegalName,
                    Lines = { s.CompanyAddress, s.CompanyEmail }
                },
                to: new EmailLayout.Party
                {
                    Label = "Bill to",
                    Name = buyerName,
                    Lines = { subscription.User?.Email ?? string.Empty, subscription.BillingAddress ?? string.Empty }
                });

            var body = $$"""
                {{partiesHtml}}

                <table>
                  <thead>
                    <tr>
                      <th>Description</th>
                      <th class="r">Amount</th>
                    </tr>
                  </thead>
                  <tbody>
                    <tr>
                      <td>{{H(productName)}} — recurring charge ({{period}})</td>
                      <td class="r">NOK {{Nok(amountInOre)}}</td>
                    </tr>
                  </tbody>
                </table>

                <div class="totals-section">
                  <table>
                    <tbody>{{subRows}}</tbody>
                    <tbody>
                      <tr class="grand">
                        <td>Total</td>
                        <td class="r">NOK {{Nok(amountInOre)}}</td>
                      </tr>
                    </tbody>
                  </table>
                </div>

                <div class="footer">
                  <p>Charged via Vipps recurring agreement.</p>
                  <p>Charge date: {{issuedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}}.</p>
                </div>
                """;

            return EmailLayout.Render(s, new EmailLayout.Meta
            {
                Number = $"#{invoiceId:D4}",
                Subtitle = $"INVOICE  ·  {issuedAt:yyyy-MM-dd}",
                Badge = "Paid",
                FootNote = $"Vipps agreement {subscription.VippsAgreementId}"
            }, body);
        }

        private static string BuildInvoiceHtml(Order order, AppSettings s, int invoiceId, DateTime issuedAt)
        {
            static string Nok(int ore) => MoneyFormat.Nok(ore);
            static string H(string? v) => HttpUtility.HtmlEncode(v ?? string.Empty);

            var vatHeaders = s.VatEnabled
                ? """<th class="r">VAT %</th><th class="r">VAT</th>"""
                : string.Empty;

            var linesSb = new StringBuilder();
            int totalExcl = 0, totalVat = 0;

            foreach (var item in order.OrderItems)
            {
                int lineIncl = item.PriceAtPurchaseInOre * item.Quantity;
                int lineExcl = item.UnitPriceExclVatInOre * item.Quantity;
                int lineVat  = lineIncl - lineExcl;
                totalExcl   += lineExcl;
                totalVat    += lineVat;

                var vatCols = s.VatEnabled
                    ? $"""<td class="r">{item.VatPercentage}%</td><td class="r">NOK {Nok(lineVat)}</td>"""
                    : string.Empty;

                linesSb.Append($"""
                    <tr>
                      <td>{H(item.ShopItem?.Name)}</td>
                      <td class="c">{item.Quantity}</td>
                      <td class="r">NOK {Nok(item.UnitPriceExclVatInOre)}</td>
                      {vatCols}
                      <td class="r">NOK {Nok(lineIncl)}</td>
                    </tr>
                    """);
            }

            var subRows = BuildVatSubRows(s.VatEnabled, totalExcl, totalVat);

            var deliveryNote = order.ShippedAt.HasValue
                ? $"Shipped on {order.ShippedAt.Value:yyyy-MM-dd}."
                : "Estimated delivery: 3–5 business days from shipment.";

            var buyerName = order.User != null ? $"{order.User.FirstName} {order.User.LastName}" : "—";

            var partiesHtml = EmailLayout.RenderParties(
                from: new EmailLayout.Party
                {
                    Label = "From",
                    Name = s.CompanyLegalName,
                    Lines = { s.CompanyAddress, s.CompanyEmail }
                },
                to: new EmailLayout.Party
                {
                    Label = "Bill to",
                    Name = buyerName,
                    Lines = { order.User?.Email ?? string.Empty, order.ShippingAddress ?? string.Empty }
                });

            var body = $$"""
                {{partiesHtml}}

                <table>
                  <thead>
                    <tr>
                      <th>Description</th>
                      <th class="c">Qty</th>
                      <th class="r">Unit price</th>
                      {{vatHeaders}}
                      <th class="r">Amount</th>
                    </tr>
                  </thead>
                  <tbody>
                    {{linesSb}}
                  </tbody>
                </table>

                <div class="totals-section">
                  <table>
                    <tbody>{{subRows}}</tbody>
                    <tbody>
                      <tr class="grand">
                        <td>Total</td>
                        <td class="r">NOK {{Nok(order.TotalInOre)}}</td>
                      </tr>
                    </tbody>
                  </table>
                </div>

                <div class="footer">
                  <p>Paid via Vipps.</p>
                  <p>Delivery address: {{H(order.ShippingAddress)}} — {{deliveryNote}}</p>
                </div>
                """;

            return EmailLayout.Render(s, new EmailLayout.Meta
            {
                Number = $"#{invoiceId:D4}",
                Subtitle = $"INVOICE  ·  {issuedAt:yyyy-MM-dd}",
                Badge = "Paid",
                FootNote = $"Vipps order #{order.Id}"
            }, body);
        }

        private static string BuildVatSubRows(bool vatEnabled, int netInOre, int vatInOre)
        {
            if (!vatEnabled) return string.Empty;
            return $"""
                    <tr class="sub"><td class="r">Subtotal excl. VAT</td><td class="r">NOK {MoneyFormat.Nok(netInOre)}</td></tr>
                    <tr class="sub"><td class="r">VAT 25%</td><td class="r">NOK {MoneyFormat.Nok(vatInOre)}</td></tr>
                """;
        }
    }
}
