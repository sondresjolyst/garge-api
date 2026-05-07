using garge_api.Helpers;
using garge_api.Models;
using garge_api.Models.Admin;
using garge_api.Models.Shop;
using Microsoft.EntityFrameworkCore;
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

            var subRows = s.VatEnabled
                ? $"""
                    <tr class="sub"><td class="r">Subtotal excl. VAT</td><td class="r">NOK {Nok(totalExcl)}</td></tr>
                    <tr class="sub"><td class="r">VAT</td><td class="r">NOK {Nok(totalVat)}</td></tr>
                  """
                : string.Empty;

            var deliveryNote = order.ShippedAt.HasValue
                ? $"Shipped on {order.ShippedAt.Value:yyyy-MM-dd}."
                : "Estimated delivery: 3–5 business days from shipment.";

            var buyerName = order.User != null ? $"{order.User.FirstName} {order.User.LastName}" : "—";

            var body = $$"""
                <div class="parties">
                  <div class="party">
                    <div class="party-label">From</div>
                    <p>
                      <strong>{{H(s.CompanyLegalName)}}</strong><br>
                      {{H(s.CompanyAddress)}}<br>
                      {{H(s.CompanyEmail)}}
                    </p>
                  </div>
                  <div class="party">
                    <div class="party-label">Bill to</div>
                    <p>
                      <strong>{{H(buyerName)}}</strong><br>
                      {{H(order.User?.Email)}}<br>
                      {{H(order.ShippingAddress)}}
                    </p>
                  </div>
                </div>

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
                  <p>Payment received via Vipps.</p>
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
    }
}
