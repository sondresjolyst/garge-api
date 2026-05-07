using garge_api.Models;
using garge_api.Models.Admin;
using garge_api.Models.Shop;
using Microsoft.EntityFrameworkCore;
using PuppeteerSharp;
using PuppeteerSharp.Media;
using System.Globalization;
using System.Text;
using System.Web;

namespace garge_api.Services
{
    public class InvoiceService : IInvoiceService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IEmailService _emailService;
        private readonly ILogger<InvoiceService> _logger;

        public InvoiceService(
            IServiceScopeFactory scopeFactory,
            IEmailService emailService,
            ILogger<InvoiceService> logger)
        {
            _scopeFactory = scopeFactory;
            _emailService = emailService;
            _logger = logger;
        }

        public async Task<int> GenerateAndStoreAsync(int orderId)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var order = await db.Orders
                .Include(o => o.User)
                .Include(o => o.OrderItems).ThenInclude(i => i.ShopItem)
                .FirstOrDefaultAsync(o => o.Id == orderId)
                ?? throw new InvalidOperationException($"Order {orderId} not found");

            var settings = await db.AppSettings.FindAsync(1) ?? new AppSettings();

            // Save first to get the sequential invoice ID, then fill PDF
            var invoice = new Invoice { OrderId = orderId, IssuedAt = DateTime.UtcNow, PdfData = [] };
            db.Invoices.Add(invoice);
            await db.SaveChangesAsync();

            var html = BuildInvoiceHtml(order, settings, invoice.Id, invoice.IssuedAt);
            invoice.PdfData = await RenderPdfAsync(html);
            await db.SaveChangesAsync();

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

        private static async Task<byte[]> RenderPdfAsync(string html)
        {
            var execPath = Environment.GetEnvironmentVariable("PUPPETEER_EXECUTABLE_PATH");
            if (string.IsNullOrEmpty(execPath))
            {
                var browserFetcher = new BrowserFetcher();
                await browserFetcher.DownloadAsync();
            }

            await using var browser = await Puppeteer.LaunchAsync(new LaunchOptions
            {
                Headless = true,
                ExecutablePath = execPath,
                Args = ["--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage"]
            });
            await using var page = await browser.NewPageAsync();
            await page.SetContentAsync(html, new NavigationOptions
            {
                WaitUntil = [WaitUntilNavigation.Networkidle0]
            });

            return await page.PdfDataAsync(new PdfOptions
            {
                Format = PaperFormat.A4,
                PrintBackground = true,
                MarginOptions = new MarginOptions { Top = "0", Bottom = "15mm", Left = "0", Right = "0" }
            });
        }

        private static string BuildInvoiceHtml(Order order, AppSettings s, int invoiceId, DateTime issuedAt)
        {
            static string Nok(int ore) => (ore / 100.0).ToString("N2", CultureInfo.InvariantCulture);
            static string H(string? v) => HttpUtility.HtmlEncode(v ?? string.Empty);

            var orgLine = s.VatEnabled ? $"{s.CompanyOrgNumber} MVA" : s.CompanyOrgNumber;

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

            var vatFootnote = s.VatEnabled
                ? string.Empty
                : "<p>Not VAT registered — below the NOK 50,000 registration threshold.</p>";

            var deliveryNote = order.ShippedAt.HasValue
                ? $"Shipped on {order.ShippedAt.Value:yyyy-MM-dd}."
                : "Estimated delivery: 3–5 business days from shipment.";

            var buyerName = order.User != null ? $"{order.User.FirstName} {order.User.LastName}" : "—";

            return $$"""
                <!DOCTYPE html>
                <html>
                <head>
                <meta charset="utf-8">
                <style>
                  * { box-sizing: border-box; margin: 0; padding: 0; }
                  body { font-family: Arial, Helvetica, sans-serif; font-size: 12px; color: #1a1a1a; background: #fff; }

                  .header-band {
                    background: #0284c7;
                    color: #fff;
                    padding: 28px 32px 20px;
                    display: flex;
                    justify-content: space-between;
                    align-items: flex-start;
                  }
                  .brand { font-size: 22px; font-weight: 700; letter-spacing: .04em; margin-bottom: 4px; }
                  .brand-sub { font-size: 11px; opacity: .85; }
                  .inv-meta { text-align: right; }
                  .inv-number { font-size: 28px; font-weight: 700; line-height: 1; margin-bottom: 4px; }
                  .inv-date { font-size: 11px; opacity: .85; margin-bottom: 6px; }
                  .badge {
                    display: inline-block;
                    background: rgba(255,255,255,0.25);
                    border: 1px solid rgba(255,255,255,0.5);
                    color: #fff;
                    padding: 2px 10px;
                    border-radius: 20px;
                    font-size: 10px;
                    font-weight: 700;
                    letter-spacing: .08em;
                    text-transform: uppercase;
                  }
                  .body { padding: 24px 32px; }
                  .parties { display: flex; gap: 40px; margin-bottom: 24px; }
                  .party { flex: 1; }
                  .party-label {
                    font-size: 9px; font-weight: 700; text-transform: uppercase;
                    letter-spacing: .1em; color: #0284c7; margin-bottom: 6px;
                  }
                  .party p { line-height: 1.6; color: #333; }
                  table { width: 100%; border-collapse: collapse; }
                  th {
                    font-size: 10px; font-weight: 700; text-transform: uppercase;
                    letter-spacing: .06em; color: #0284c7;
                    border-bottom: 2px solid #0284c7;
                    padding: 7px 8px; text-align: left;
                  }
                  td { padding: 7px 8px; border-bottom: 1px solid #e5e7eb; }
                  tr:last-child td { border-bottom: none; }
                  tbody tr:nth-child(even) { background: #f8fafc; }
                  .r { text-align: right; }
                  .c { text-align: center; }
                  .totals-section { margin-top: 8px; border-top: 2px solid #e5e7eb; padding-top: 8px; }
                  .totals-section table { width: 40%; margin-left: auto; }
                  .sub td { padding: 3px 8px; color: #555; }
                  .grand td {
                    padding: 7px 8px; font-weight: 700; font-size: 14px;
                    border-top: 2px solid #0284c7; color: #0284c7;
                  }
                  .footer {
                    margin: 24px 32px 0;
                    padding-top: 12px;
                    border-top: 1px solid #e5e7eb;
                    font-size: 10px; color: #888; line-height: 1.6;
                  }
                  .footer p + p { margin-top: 2px; }
                </style>
                </head>
                <body>

                <div class="header-band">
                  <div>
                    <div class="brand">{{H(s.CompanyName)}}</div>
                    <div class="brand-sub">
                      {{H(s.CompanyLegalName)}}<br>
                      Org. no. {{H(orgLine)}}<br>
                      {{H(s.CompanyAddress)}}<br>
                      {{H(s.CompanyEmail)}}
                    </div>
                  </div>
                  <div class="inv-meta">
                    <div class="inv-number">#{{invoiceId:D4}}</div>
                    <div class="inv-date">INVOICE &nbsp;·&nbsp; {{issuedAt:yyyy-MM-dd}}</div>
                    <span class="badge">Paid</span>
                    <div style="margin-top:6px;font-size:10px;opacity:.75;">Vipps order #{{order.Id}}</div>
                  </div>
                </div>

                <div class="body">
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
                </div>

                <div class="footer">
                  <p>Payment received via Vipps.</p>
                  <p>Delivery address: {{H(order.ShippingAddress)}} — {{deliveryNote}}</p>
                  {{vatFootnote}}
                </div>

                </body>
                </html>
                """;
        }
    }
}
