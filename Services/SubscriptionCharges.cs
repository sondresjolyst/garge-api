using System.Globalization;
using System.Text.RegularExpressions;
using garge_api.Models.Subscription;

namespace garge_api.Services
{
    /// <summary>
    /// Charge keys and billing dates for recurring subscriptions. The key is sent to Vipps as the
    /// charge's orderId, which Vipps uses as the charge id in its webhooks, so an event tells which
    /// subscription, due date and attempt it is about.
    /// </summary>
    public static partial class SubscriptionCharges
    {
        /// <summary>Charge attempts for one billing period before the agreement is stopped.</summary>
        public const int MaxAttempts = 3;

        /// <summary>
        /// The Vipps failureReason for a charge above the maximum amount the customer approved. The
        /// customer has to approve a higher amount in the Vipps app, so the usual advice to update the
        /// payment method does not apply.
        /// </summary>
        public const string AmountTooHigh = "charge_amount_too_high";

        /// <summary>The highest suggestedMaxAmount Vipps accepts on a NOK agreement, 20 000 kr.</summary>
        public const int MaxAgreementAmountInOre = 2_000_000;

        // The first attempt has no attempt suffix.
        public static string Key(int subscriptionId, DateTime due, int attempt) => attempt == 0
            ? $"charge-{subscriptionId}-{due.Ticks}"
            : $"charge-{subscriptionId}-{due.Ticks}-r{attempt}";

        [GeneratedRegex(@"\Acharge-(?<sub>[0-9]{1,10})-(?<ticks>[0-9]{1,19})(?:-r(?<attempt>[0-9]{1,3}))?\z")]
        private static partial Regex KeyPattern();

        public static bool TryParse(string? chargeId, out int subscriptionId, out DateTime due, out int attempt)
        {
            subscriptionId = 0;
            due = default;
            attempt = 0;
            if (string.IsNullOrEmpty(chargeId)) return false;
            var m = KeyPattern().Match(chargeId);
            if (!m.Success
                || !int.TryParse(m.Groups["sub"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out subscriptionId)
                || !long.TryParse(m.Groups["ticks"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || ticks > DateTime.MaxValue.Ticks)
                return false;
            if (m.Groups["attempt"].Success
                && (!int.TryParse(m.Groups["attempt"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out attempt) || attempt < 1))
                return false;
            due = new DateTime(ticks, DateTimeKind.Utc);
            return true;
        }

        /// <summary>
        /// The first billing date after <paramref name="after"/>, counted in whole intervals from the
        /// anchor date. Counting from the anchor keeps the day of the month, so a subscription started
        /// on the 31st is charged on the last day of shorter months and on the 31st again after them.
        /// </summary>
        public static DateTime NextDue(DateTime anchor, DateTime after, BillingInterval interval)
        {
            var start = DateTime.SpecifyKind(anchor.Date, DateTimeKind.Utc);
            var afterDate = after.Date;
            if (interval == BillingInterval.Yearly)
            {
                var years = Math.Max(1, afterDate.Year - start.Year);
                while (start.AddYears(years) <= afterDate) years++;
                while (years > 1 && start.AddYears(years - 1) > afterDate) years--;
                return start.AddYears(years);
            }

            var months = Math.Max(1, (afterDate.Year - start.Year) * 12 + afterDate.Month - start.Month);
            while (start.AddMonths(months) <= afterDate) months++;
            while (months > 1 && start.AddMonths(months - 1) > afterDate) months--;
            return start.AddMonths(months);
        }
    }
}
