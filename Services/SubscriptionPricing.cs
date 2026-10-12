using garge_api.Models.Subscription;

namespace garge_api.Services
{
    /// <summary>What a subscriber pays, and when a price increase may reach them.</summary>
    public static class SubscriptionPricing
    {
        /// <summary>
        /// The least notice a price increase gets. digitalytelsesloven § 28 asks for notice in reasonable
        /// time, and with monthly billing this gives every subscriber a charge date to cancel before.
        /// </summary>
        public static readonly TimeSpan MinNotice = TimeSpan.FromDays(30);

        /// <summary>The earliest effective date an increase announced now may have.</summary>
        public static DateTime EarliestEffectiveFrom(DateTime now) => now.Date + MinNotice;

        /// <summary>When a subscriber notified at <paramref name="notifiedAt"/> starts paying the new price.</summary>
        public static DateTime EffectiveFor(PriceChange change, DateTime notifiedAt)
        {
            var earliest = EarliestEffectiveFrom(notifiedAt);
            return change.EffectiveFrom.Date > earliest ? change.EffectiveFrom.Date : earliest;
        }

        /// <summary>
        /// The unit price of a charge due on <paramref name="dueDate"/>: the new price of the latest notice
        /// in effect by then and not yet applied, or else the agreed price. Charges are posted up to a
        /// week early, so the due date decides, not the day the charge is posted.
        /// </summary>
        public static int UnitPriceFor(Subscription sub, IEnumerable<PriceChangeNotice> notices, DateTime dueDate) =>
            notices
                .Where(n => n.SubscriptionId == sub.Id && n.AppliedAt == null && n.EffectiveFrom != null
                            && n.EffectiveFrom.Value.Date <= dueDate.Date
                            && n.PriceChange != null && n.PriceChange.CancelledAt == null)
                .OrderByDescending(n => n.EffectiveFrom)
                .Select(n => (int?)n.PriceChange!.NewPriceInOre)
                .FirstOrDefault() ?? sub.UnitPriceInOre;

        /// <summary>
        /// The unit price a Vipps agreement's ceiling must allow: the agreed price, or a notified
        /// increase not yet in effect, whichever is higher.
        /// </summary>
        public static int CeilingUnitPrice(Subscription sub, IEnumerable<PriceChangeNotice> notices) =>
            notices
                .Where(n => n.SubscriptionId == sub.Id && n.AppliedAt == null && n.NotifiedAt != null
                            && n.PriceChange != null && n.PriceChange.CancelledAt == null)
                .Select(n => n.PriceChange!.NewPriceInOre)
                .Append(sub.UnitPriceInOre)
                .Max();
    }
}
