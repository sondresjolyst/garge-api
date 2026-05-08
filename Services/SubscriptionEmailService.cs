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

        public Task SendActivatedAsync(int subscriptionId) =>
            SendAsync(subscriptionId, kind: "activated",
                subjectFormat: "Subscription confirmed — {0}",
                buildBody: (sub, s) =>
                {
                    var productName = sub.Product?.Name ?? "Garge subscription";
                    var period = sub.Product?.Interval == BillingInterval.Yearly ? "yearly" : "monthly";
                    var price = sub.Product != null ? MoneyFormat.Nok(sub.Product.PriceInOre) : null;
                    var nextChargeLine = sub.NextChargeDate.HasValue
                        ? $"<p>Next charge: <strong>{sub.NextChargeDate.Value:yyyy-MM-dd}</strong>.</p>"
                        : string.Empty;

                    return $$"""
                        <h1>Welcome aboard, {{H(sub.User?.FirstName)}}!</h1>
                        <p>Your <strong>{{H(productName)}}</strong> {{period}} subscription is active. Vipps will charge {{(price != null ? $"NOK {price}" : "the agreed amount")}} per {{(period == "yearly" ? "year" : "month")}}.</p>
                        {{nextChargeLine}}
                        <p>Receipts for every charge land here as PDF invoices.</p>
                        """;
                });

        public Task SendChargeFailedAsync(int subscriptionId) =>
            SendAsync(subscriptionId, kind: "charge-failed",
                subjectFormat: "Action needed: subscription payment failed — {0}",
                buildBody: (sub, s) =>
                {
                    var productName = sub.Product?.Name ?? "Garge subscription";
                    return $$"""
                        <h1>We couldn't charge your subscription</h1>
                        <p>Hi {{H(sub.User?.FirstName)}}, the latest charge for <strong>{{H(productName)}}</strong> failed. Vipps will retry, but please update your payment method in the Vipps app to avoid the agreement being stopped.</p>
                        <p>If you've already fixed it, you can ignore this email.</p>
                        """;
                });

        private async Task SendAsync(
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
                return;
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
                await _emailService.SendEmailAsync(
                    subscription.User.Email,
                    string.Format(subjectFormat, settings.CompanyName),
                    html);
                _logger.LogInformation("Subscription {Kind} email sent for subscription {SubscriptionId}",
                    kind, subscriptionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send subscription {Kind} email for subscription {SubscriptionId}",
                    kind, subscriptionId);
            }
        }

        private static string H(string? v) => HttpUtility.HtmlEncode(v ?? string.Empty);
    }
}
