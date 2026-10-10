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
            if (invoice != null && !force && !NeedsPdf(invoice))
            {
                _logger.LogInformation("Invoice {InvoiceId} already exists or is in progress for order {OrderId}, skipped", invoice.Id, orderId);
                return invoice.Id;
            }

            var wasNewRow = invoice == null;
            if (invoice == null)
            {
                invoice = new Invoice
                {
                    OrderId = orderId, AmountInOre = order.TotalInOre, IssuedAt = DateTime.UtcNow, PdfData = [],
                    VatPercentage = order.OrderItems.Select(i => i.VatPercentage).DefaultIfEmpty(0).Max()
                };
                db.Invoices.Add(invoice);
                await db.SaveChangesAsync();
            }

            if (wasNewRow) await CheckVatThresholdAsync(scope);

            var html = BuildInvoiceHtml(order, settings, invoice.Id, invoice.IssuedAt);
            await RenderAsync(db, invoice, html);

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
            if (existing != null && !NeedsPdf(existing))
            {
                _logger.LogInformation("Invoice {InvoiceId} already exists for charge {ChargeId}, skipped",
                    existing.Id, vippsChargeId);
                return existing.Id;
            }

            var subscription = await db.Subscriptions
                .Include(s => s.User)
                .Include(s => s.Product)
                .FirstOrDefaultAsync(s => s.Id == subscriptionId)
                ?? throw new InvalidOperationException($"Subscription {subscriptionId} not found");

            // Fallback: if the agreement-activated webhook hasn't populated
            // BillingAddress yet (race with first charge), fetch from Vipps now.
            // Vipps service may be absent in test setups; skip silently then.
            if (existing == null && string.IsNullOrEmpty(subscription.BillingAddress))
            {
                var vipps = scope.ServiceProvider.GetService<IVippsService>();
                if (vipps != null)
                {
                    try
                    {
                        var details = await vipps.GetAgreementAsync(subscription.VippsAgreementId, subscription.IsTest);
                        if (!string.IsNullOrEmpty(details?.Sub))
                        {
                            var info = await vipps.GetUserInfoAsync(details.Sub, subscription.IsTest);
                            var formatted = VippsAddressFormatter.Format(info?.Address);
                            if (!string.IsNullOrEmpty(formatted))
                            {
                                subscription.BillingAddress = formatted;
                                await db.SaveChangesAsync();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Invoice fallback: failed to fetch Vipps billing address for subscription {SubscriptionId}", subscription.Id);
                    }
                }
            }

            var settings = await db.AppSettings.FindAsync(1) ?? new AppSettings();

            var invoice = existing;
            if (invoice == null)
            {
                invoice = new Invoice
                {
                    SubscriptionId = subscription.Id,
                    VippsChargeId = vippsChargeId,
                    AmountInOre = amountInOre,
                    IssuedAt = occurredAt,
                    PdfData = [],
                    VatPercentage = Pricing.VatPercentFor(settings.VatEnabled)
                };
                db.Invoices.Add(invoice);
                await db.SaveChangesAsync();
                await CheckVatThresholdAsync(scope);
            }

            var html = BuildSubscriptionInvoiceHtml(subscription, settings, invoice.Id, invoice.IssuedAt, invoice.AmountInOre, invoice.VatPercentage);
            await RenderAsync(db, invoice, html);

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

        /// <summary>How long a PDF counts as being made before the retry job tries it again.</summary>
        internal static readonly TimeSpan PdfRetryAfter = TimeSpan.FromMinutes(5);

        private static bool NeedsPdf(Invoice invoice) =>
            invoice.PdfData.Length == 0
            && (invoice.PdfAttemptedAt == null || DateTime.UtcNow - invoice.PdfAttemptedAt >= PdfRetryAfter);

        // The invoice row stays when the PDF fails, so the sale is never lost. The retry job makes it later.
        private async Task RenderAsync(ApplicationDbContext db, Invoice invoice, string html)
        {
            invoice.PdfAttemptedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            try
            {
                invoice.PdfData = await _pdfRenderer.RenderAsync(html);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PDF for invoice {InvoiceId} failed, it is retried later", invoice.Id);
                throw;
            }
            await db.SaveChangesAsync();
        }

        public async Task<int> RetryMissingPdfsAsync(CancellationToken ct = default)
        {
            List<(int Id, int? OrderId, int? SubscriptionId, string? ChargeId, int Amount, DateTime IssuedAt)> pending;
            List<int> corrections;
            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var cutoff = DateTime.UtcNow - PdfRetryAfter;
                pending = (await db.Invoices.AsNoTracking()
                        .Where(i => i.PdfData.Length == 0 && i.CreditsInvoiceId == null && i.ReplacesInvoiceId == null
                                    && (i.PdfAttemptedAt == null || i.PdfAttemptedAt <= cutoff))
                        .Select(i => new { i.Id, i.OrderId, i.SubscriptionId, i.VippsChargeId, i.AmountInOre, i.IssuedAt })
                        .ToListAsync(ct))
                    .Select(i => (i.Id, i.OrderId, i.SubscriptionId, i.VippsChargeId, i.AmountInOre, i.IssuedAt))
                    .ToList();
                // VAT corrections whose PDFs or email did not go through.
                corrections = await db.Invoices.AsNoTracking()
                    .Where(i => i.VatCorrectedAt != null && i.VatCorrectionEmailedAt == null && i.VatCorrectedAt <= cutoff)
                    .Select(i => i.Id)
                    .ToListAsync(ct);
            }

            var made = 0;
            foreach (var p in pending)
            {
                try
                {
                    if (p.OrderId is { } orderId)
                        await GenerateAndStoreAsync(orderId);
                    else if (p.SubscriptionId is { } subscriptionId && p.ChargeId != null)
                        await GenerateForSubscriptionChargeAsync(subscriptionId, p.ChargeId, p.Amount, p.IssuedAt);
                    else
                        continue;
                    made++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Retrying the PDF for invoice {InvoiceId} failed", p.Id);
                }
            }
            foreach (var originalId in corrections)
            {
                try
                {
                    await FinishVatCorrectionAsync(originalId, ct);
                    made++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Retrying the VAT correction for invoice {InvoiceId} failed", originalId);
                }
            }
            return made;
        }

        // A new sale may bring turnover up to a warning level. A failed check never fails the invoice.
        private async Task CheckVatThresholdAsync(IServiceScope scope)
        {
            var vat = scope.ServiceProvider.GetService<IVatThresholdService>();
            if (vat == null) return;
            try { await vat.CheckAsync(DateTime.UtcNow); }
            catch (Exception ex) { _logger.LogError(ex, "VAT threshold check failed"); }
        }

        public async Task<int> GenerateVatCorrectionsAsync(CancellationToken ct = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var vat = scope.ServiceProvider.GetRequiredService<IVatThresholdService>();

            var settings = await db.AppSettings.FindAsync([1], ct) ?? new AppSettings();
            if (!settings.VatEnabled)
                throw new InvalidOperationException("VAT corrections are made after VAT registration, with VAT on.");

            await CorrectionGate.WaitAsync(ct);
            try
            {
                var status = await vat.GetStatusAsync(DateTime.UtcNow, ct);
                var made = 0;
                foreach (var sale in status.Owed.Where(o => o.CorrectedAt == null).OrderBy(o => o.IssuedAt).ThenBy(o => o.InvoiceId))
                {
                    var original = await db.Invoices.FirstAsync(i => i.Id == sale.InvoiceId, ct);
                    if (original.VatCorrectedAt != null) continue;

                    // The credit note, the new invoice and the mark on the original are saved together.
                    var now = DateTime.UtcNow;
                    var creditNote = new Invoice
                    {
                        Kind = InvoiceKind.CreditNote, CreditsInvoiceId = original.Id,
                        AmountInOre = -original.AmountInOre, VatPercentage = 0, IssuedAt = now, PdfData = []
                    };
                    var replacement = new Invoice
                    {
                        Kind = InvoiceKind.Invoice, ReplacesInvoiceId = original.Id,
                        AmountInOre = sale.AmountInOre, VatPercentage = Pricing.VatPercent, IssuedAt = now, PdfData = []
                    };
                    db.Invoices.AddRange(creditNote, replacement);
                    original.VatCorrectedAt = now;
                    await db.SaveChangesAsync(ct);
                    made++;

                    try { await FinishVatCorrectionAsync(original.Id, ct); }
                    catch (Exception ex) { _logger.LogError(ex, "VAT correction for invoice {InvoiceId} is retried later", original.Id); }
                }
                _logger.LogInformation("Made {Count} VAT corrections", made);
                return made;
            }
            finally
            {
                CorrectionGate.Release();
            }
        }

        // Corrections are made one run at a time, so no sale is corrected twice.
        private static readonly SemaphoreSlim CorrectionGate = new(1, 1);

        /// <summary>
        /// Makes the missing PDFs of a sale's credit note and new invoice, then emails both to the customer
        /// once. A salgsdokument has to be sent to the buyer (bokføringsforskriften § 5-2-1).
        /// </summary>
        private async Task FinishVatCorrectionAsync(int originalId, CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var settings = await db.AppSettings.FindAsync([1], ct) ?? new AppSettings();

            var original = await db.Invoices
                .Include(i => i.Order).ThenInclude(o => o!.User)
                .Include(i => i.Order).ThenInclude(o => o!.OrderItems).ThenInclude(oi => oi.ShopItem)
                .Include(i => i.Subscription).ThenInclude(s => s!.User)
                .Include(i => i.Subscription).ThenInclude(s => s!.Product)
                .FirstAsync(i => i.Id == originalId, ct);
            if (original.VatCorrectionEmailedAt != null) return;
            var creditNote = await db.Invoices.FirstAsync(i => i.CreditsInvoiceId == originalId, ct);
            var replacement = await db.Invoices.FirstAsync(i => i.ReplacesInvoiceId == originalId, ct);

            var creditHtml = BuildCreditNoteHtml(original, creditNote, replacement, settings);
            var invoiceHtml = BuildReplacementInvoiceHtml(original, replacement, creditNote, settings);
            if (creditNote.PdfData.Length == 0) await RenderAsync(db, creditNote, creditHtml);
            if (replacement.PdfData.Length == 0) await RenderAsync(db, replacement, invoiceHtml);

            var buyerEmail = (original.Order?.User ?? original.Subscription?.User)?.Email;
            if (!string.IsNullOrEmpty(buyerEmail))
            {
                await _emailService.SendEmailAsync(
                    buyerEmail,
                    $"{settings.CompanyName}: invoice #{replacement.Id:D4} with VAT replaces invoice #{original.Id:D4}, nothing more to pay",
                    invoiceHtml,
                    [
                        new EmailAttachment { FileName = $"credit-note-{creditNote.Id:D4}.pdf", Content = creditNote.PdfData, ContentType = "application/pdf" },
                        new EmailAttachment { FileName = $"invoice-{replacement.Id:D4}.pdf", Content = replacement.PdfData, ContentType = "application/pdf" }
                    ]);
            }
            original.VatCorrectionEmailedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        private static (string Name, List<string> Lines) Buyer(Invoice original)
        {
            var user = original.Order?.User ?? original.Subscription?.User;
            var name = user != null ? $"{user.FirstName} {user.LastName}" : "Customer";
            var address = original.Order?.ShippingAddress ?? original.Subscription?.BillingAddress;
            return (name, [user?.Email ?? string.Empty, address ?? string.Empty]);
        }

        // What was sold, from the original sale.
        private static List<(string Description, int Quantity)> Goods(Invoice original) =>
            original.Order != null
                ? original.Order.OrderItems.Select(i => (i.ShopItem?.Name ?? "Item", i.Quantity)).ToList()
                : [($"{original.Subscription?.Product?.Name ?? "Subscription"}, recurring charge ({(original.Subscription?.Product?.Interval == BillingInterval.Yearly ? "year" : "month")})", 1)];

        private static string Parties(AppSettings s, Invoice original)
        {
            var (name, lines) = Buyer(original);
            var to = new EmailLayout.Party { Label = "Bill to", Name = name };
            foreach (var line in lines) to.Lines.Add(line);
            return EmailLayout.RenderParties(
                from: new EmailLayout.Party { Label = "From", Name = s.CompanyLegalName, Lines = { s.CompanyAddress, s.CompanyEmail } },
                to: to);
        }

        private static string BuildCreditNoteHtml(Invoice original, Invoice creditNote, Invoice replacement, AppSettings s)
        {
            static string Nok(int ore) => MoneyFormat.Nok(ore);
            static string H(string? v) => HttpUtility.HtmlEncode(v ?? string.Empty);
            var saleDate = LocalTime.Date(original.IssuedAt).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var goods = string.Join(", ", Goods(original).Select(g => $"{g.Quantity} x {g.Description}"));

            var body = $$"""
                {{Parties(s, original)}}

                <table>
                  <thead>
                    <tr>
                      <th>Description</th>
                      <th class="r">Amount</th>
                    </tr>
                  </thead>
                  <tbody>
                    <tr>
                      <td>Credit for invoice #{{original.Id.ToString("D4", CultureInfo.InvariantCulture)}} of {{saleDate}}: {{H(goods)}}</td>
                      <td class="r">NOK {{Nok(creditNote.AmountInOre)}}</td>
                    </tr>
                  </tbody>
                </table>

                <div class="totals-section">
                  <table>
                    <tbody>
                      <tr class="grand">
                        <td>Total credited</td>
                        <td class="r">NOK {{Nok(creditNote.AmountInOre)}}</td>
                      </tr>
                    </tbody>
                  </table>
                </div>

                <div class="footer">
                  <p>This credit note cancels invoice #{{original.Id.ToString("D4", CultureInfo.InvariantCulture)}}, which was issued without VAT before VAT registration. Invoice #{{replacement.Id.ToString("D4", CultureInfo.InvariantCulture)}} replaces it with VAT included in the same price. Nothing is paid back and nothing more is owed.</p>
                </div>
                """;

            return EmailLayout.Render(s, new EmailLayout.Meta
            {
                Number = $"#{creditNote.Id:D4}",
                Subtitle = $"CREDIT NOTE  ·  {LocalTime.Date(creditNote.IssuedAt):yyyy-MM-dd}",
                FootNote = $"Credits invoice #{original.Id:D4}"
            }, body, vatRegistered: true);
        }

        private static string BuildReplacementInvoiceHtml(Invoice original, Invoice replacement, Invoice creditNote, AppSettings s)
        {
            static string Nok(int ore) => MoneyFormat.Nok(ore);
            static string H(string? v) => HttpUtility.HtmlEncode(v ?? string.Empty);
            var saleDate = LocalTime.Date(original.IssuedAt).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var (exclVat, vatAmount) = Pricing.Split(replacement.AmountInOre, replacement.VatPercentage);
            var goodsRows = string.Join("", Goods(original).Select(g => $"""
                    <tr>
                      <td>{H(g.Description)}</td>
                      <td class="c">{g.Quantity}</td>
                    </tr>
                """));

            var body = $$"""
                {{Parties(s, original)}}

                <table>
                  <thead>
                    <tr>
                      <th>Description</th>
                      <th class="c">Qty</th>
                    </tr>
                  </thead>
                  <tbody>{{goodsRows}}</tbody>
                </table>

                <div class="totals-section">
                  <table>
                    <tbody>{{BuildVatSubRows(replacement.VatPercentage, exclVat, vatAmount)}}</tbody>
                    <tbody>
                      <tr class="grand">
                        <td>Total, already paid</td>
                        <td class="r">NOK {{Nok(replacement.AmountInOre)}}</td>
                      </tr>
                    </tbody>
                  </table>
                </div>

                <div class="footer">
                  <p>Delivered {{saleDate}}. This invoice replaces invoice #{{original.Id.ToString("D4", CultureInfo.InvariantCulture)}}, credited by credit note #{{creditNote.Id.ToString("D4", CultureInfo.InvariantCulture)}}. The sale was made after turnover passed the VAT registration threshold, so the price that was paid includes 25% VAT. It was paid in full and there is nothing more to pay.</p>
                </div>
                """;

            return EmailLayout.Render(s, new EmailLayout.Meta
            {
                Number = $"#{replacement.Id:D4}",
                Subtitle = $"INVOICE  ·  {LocalTime.Date(replacement.IssuedAt):yyyy-MM-dd}",
                Badge = "Paid",
                FootNote = $"Replaces invoice #{original.Id:D4}"
            }, body, vatRegistered: true);
        }

        private static string BuildSubscriptionInvoiceHtml(
            Subscription subscription, AppSettings s, int invoiceId, DateTime issuedAt, int amountInOre, int vatPercent)
        {
            static string Nok(int ore) => MoneyFormat.Nok(ore);
            static string H(string? v) => HttpUtility.HtmlEncode(v ?? string.Empty);

            var product = subscription.Product;
            var productName = product?.Name ?? "Subscription";
            var period = product?.Interval == BillingInterval.Yearly ? "year" : "month";
            var buyerName = subscription.User != null
                ? $"{subscription.User.FirstName} {subscription.User.LastName}"
                : "—";

            var (net, vatAmount) = Pricing.Split(amountInOre, vatPercent);
            var subRows = BuildVatSubRows(vatPercent, net, vatAmount);

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
            }, body, vatRegistered: vatPercent > 0);
        }

        private static string BuildInvoiceHtml(Order order, AppSettings s, int invoiceId, DateTime issuedAt)
        {
            static string Nok(int ore) => MoneyFormat.Nok(ore);
            static string H(string? v) => HttpUtility.HtmlEncode(v ?? string.Empty);

            // VAT columns follow the rate each line was sold at, not today's setting.
            var vatPercent = order.OrderItems.Select(i => i.VatPercentage).DefaultIfEmpty(0).Max();
            var showVat = vatPercent > 0;
            var vatHeaders = showVat
                ? """<th class="r">VAT %</th><th class="r">VAT</th>"""
                : string.Empty;

            var linesSb = new StringBuilder();
            int totalExcl = 0, totalVat = 0;

            foreach (var item in order.OrderItems)
            {
                int lineIncl = item.PriceAtPurchaseInOre * item.Quantity;
                var (lineExcl, lineVat) = Pricing.Split(lineIncl, item.VatPercentage);
                totalExcl   += lineExcl;
                totalVat    += lineVat;

                var vatCols = showVat
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

            var subRows = BuildVatSubRows(vatPercent, totalExcl, totalVat);

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
            }, body, vatRegistered: vatPercent > 0);
        }

        private static string BuildVatSubRows(int vatPercent, int netInOre, int vatInOre)
        {
            if (vatPercent == 0) return string.Empty;
            return $"""
                    <tr class="sub"><td class="r">Subtotal excl. VAT</td><td class="r">NOK {MoneyFormat.Nok(netInOre)}</td></tr>
                    <tr class="sub"><td class="r">VAT {vatPercent}%</td><td class="r">NOK {MoneyFormat.Nok(vatInOre)}</td></tr>
                """;
        }
    }
}
