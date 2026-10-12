using garge_api.Helpers;
using garge_api.Models;
using garge_api.Models.Admin;
using garge_api.Models.Subscription;
using Microsoft.EntityFrameworkCore;
using System.Web;

namespace garge_api.Services
{
    public class SubscriptionEmailService : ISubscriptionEmailService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IEmailService _emailService;
        private readonly ILogger<SubscriptionEmailService> _logger;

        public SubscriptionEmailService(
            IServiceScopeFactory scopeFactory,
            IEmailService emailService,
            ILogger<SubscriptionEmailService> logger)
        {
            _scopeFactory = scopeFactory;
            _emailService = emailService;
            _logger = logger;
        }

        public Task<bool> SendActivatedAsync(int subscriptionId) =>
            SendAsync(subscriptionId, kind: "activated",
                subjectFormat: "Subscription confirmed — {0}",
                buildBody: (sub, s) => BuildSubscriptionEmailBody(
                    sub, s,
                    headline: $"Welcome aboard, {H(sub.User?.FirstName)}!",
                    intro: "Your subscription is now active.",
                    footerNote: BuildActivatedFooter(sub)));

        public Task<bool> SendChargeFailedAsync(int subscriptionId) =>
            SendAsync(subscriptionId, kind: "charge-failed",
                subjectFormat: "Action needed: subscription payment failed — {0}",
                buildBody: (sub, s) => BuildSubscriptionEmailBody(
                    sub, s,
                    headline: "We couldn't charge your subscription",
                    intro: sub.LastChargeFailureReason == SubscriptionCharges.AmountTooHigh
                        ? $"Hi {sub.User?.FirstName}, the latest charge is above the maximum amount you approved for this agreement in Vipps. Open the Vipps app and approve the new amount. We'll try the charge again."
                        : $"Hi {sub.User?.FirstName}, the latest charge failed. We'll retry automatically — update your payment method in the Vipps app to avoid the agreement being stopped.",
                    footerNote: "If you've already fixed it, you can ignore this email."));

        public Task<bool> SendStoppedForNonPaymentAsync(int subscriptionId) =>
            SendAsync(subscriptionId, kind: "stopped-non-payment",
                subjectFormat: "Subscription stopped, payment failed: {0}",
                buildBody: (sub, s) => BuildSubscriptionEmailBody(
                    sub, s,
                    headline: "Your subscription has been stopped",
                    intro: sub.LastChargeFailureReason == SubscriptionCharges.AmountTooHigh
                        ? $"Hi {sub.User?.FirstName}, the charge for your subscription stayed above the maximum amount approved in your Vipps agreement, even after retrying over several days, so the agreement has been stopped."
                        : $"Hi {sub.User?.FirstName}, we could not charge your subscription, even after retrying over several days, so the Vipps agreement has been stopped.",
                    footerNote: "You can subscribe again under Billing at any time."));

        public Task<bool> SendPriceChangeNoticeAsync(int subscriptionId, int oldUnitPriceInOre, int newUnitPriceInOre, DateTime effectiveFrom) =>
            SendAsync(subscriptionId, kind: "price-change",
                subjectFormat: "Price change for your subscription: {0}",
                buildBody: (sub, s) => BuildSubscriptionEmailBody(
                    sub, s,
                    headline: "Your subscription price is changing",
                    intro: $"Hi {sub.User?.FirstName}, the price of {sub.Product?.Name ?? "your subscription"} goes up from NOK {MoneyFormat.Nok(oldUnitPriceInOre * sub.Quantity)} to NOK {MoneyFormat.Nok(newUnitPriceInOre * sub.Quantity)} per {Period(sub)}. The new price applies to charges due on or after {effectiveFrom:yyyy-MM-dd}. Charges due before then keep the current price.",
                    footerNote: $"You may cancel at no cost under Billing, also on or after {effectiveFrom:yyyy-MM-dd}. If the new amount is above the maximum you approved in Vipps, the Vipps app asks you to approve it at the first charge. If you do not approve it, the charge fails.",
                    unitPriceInOre: newUnitPriceInOre));

        private static string BuildActivatedFooter(Subscription sub)
        {
            var parts = new List<string>();
            if (sub.NextChargeDate.HasValue)
                parts.Add($"Next charge on {sub.NextChargeDate.Value:yyyy-MM-dd}.");
            parts.Add("Cancel anytime under Billing.");
            if (sub.ConsentAcceptedAt.HasValue)
                parts.Add($"You waived your 14-day right of withdrawal at signup on {sub.ConsentAcceptedAt.Value:yyyy-MM-dd}, so service is delivered immediately.");
            return string.Join(" ", parts);
        }

        private static string BuildSubscriptionEmailBody(
            Subscription sub, AppSettings s, string headline, string intro, string footerNote, int? unitPriceInOre = null)
        {
            var productName = sub.Product?.Name ?? "Garge subscription";
            var period = Period(sub);
            var price = MoneyFormat.Nok((unitPriceInOre ?? sub.UnitPriceInOre) * sub.Quantity);
            var buyerName = ((sub.User?.FirstName ?? string.Empty) + " " + (sub.User?.LastName ?? string.Empty)).Trim();
            var amountCell = $"NOK {price} / {period}";

            var partiesHtml = EmailLayout.RenderParties(
                from: new EmailLayout.Party
                {
                    Label = "From",
                    Name = s.CompanyLegalName,
                    Lines = { s.CompanyAddress, s.CompanyEmail }
                },
                to: new EmailLayout.Party
                {
                    Label = "Subscriber",
                    Name = buyerName,
                    Lines = { sub.User?.Email ?? string.Empty, sub.BillingAddress ?? string.Empty }
                });

            return $$"""
                <h1>{{headline}}</h1>
                <p>{{H(intro)}}</p>

                {{partiesHtml}}

                <table>
                  <thead>
                    <tr>
                      <th>Subscription</th>
                      <th class="r">Amount</th>
                    </tr>
                  </thead>
                  <tbody>
                    <tr>
                      <td>{{H(productName)}} — {{period}}ly</td>
                      <td class="r">{{H(amountCell)}}</td>
                    </tr>
                  </tbody>
                </table>

                <div class="footer">
                  <p>{{H(footerNote)}}</p>
                </div>
                """;
        }

        private static string Period(Subscription sub) => sub.Product?.Interval == BillingInterval.Yearly ? "year" : "month";

        private async Task<bool> SendAsync(
            int subscriptionId,
            string kind,
            string subjectFormat,
            Func<Subscription, AppSettings, string> buildBody)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var subscription = await db.Subscriptions
                .Include(s => s.User)
                .Include(s => s.Product)
                .FirstOrDefaultAsync(s => s.Id == subscriptionId);

            if (subscription?.User?.Email == null)
            {
                _logger.LogWarning("Subscription {SubscriptionId} missing user/email — {Kind} email skipped",
                    subscriptionId, kind);
                return false;
            }

            var settings = await db.AppSettings.FindAsync(1) ?? new AppSettings();

            var body = buildBody(subscription, settings);
            var html = EmailLayout.Render(settings, new EmailLayout.Meta
            {
                Number = $"#{subscription.Id}",
                Subtitle = $"SUBSCRIPTION  ·  {DateTime.UtcNow:yyyy-MM-dd}",
            }, body);

            try
            {
                if (!await _emailService.SendEmailAsync(
                    subscription.User.Email,
                    string.Format(subjectFormat, settings.CompanyName),
                    html))
                    return false;
                _logger.LogInformation("Subscription {Kind} email sent for subscription {SubscriptionId}",
                    kind, subscriptionId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send subscription {Kind} email for subscription {SubscriptionId}",
                    kind, subscriptionId);
                return false;
            }
        }

        private static string H(string? v) => HttpUtility.HtmlEncode(v ?? string.Empty);
    }
}
