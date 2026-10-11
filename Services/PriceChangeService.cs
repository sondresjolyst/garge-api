using garge_api.Models;
using garge_api.Models.Subscription;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace garge_api.Services
{
    public interface IPriceChangeService
    {
        /// <summary>Schedules an increase of a product's price. Returns an error message when it cannot.</summary>
        Task<(PriceChange? Change, string? Error)> ScheduleAsync(int productId, int newPriceInOre, DateTime effectiveFrom, string? adminUserId, CancellationToken ct = default);

        /// <summary>Calls off a change before it took effect for anyone. Returns an error message when it cannot.</summary>
        Task<string?> CancelAsync(int priceChangeId, CancellationToken ct = default);

        /// <summary>
        /// Checks an admin's direct edit of a product's price and interval. An increase or a new interval
        /// is refused while anyone subscribes, as it needs notice. A decrease lowers every agreed price
        /// above it at once. Changes are tracked, not saved. Returns an error message when refused.
        /// </summary>
        Task<string?> PrepareDirectEditAsync(Product product, int newPriceInOre, BillingInterval newInterval, CancellationToken ct = default);

        /// <summary>Sends due notices, then moves subscribers and products to the new price once it is in effect.</summary>
        Task SweepAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Price increases with notice, as digitalytelsesloven § 28 asks: every subscriber is emailed, and
    /// pays the new price only from 30 days after their notice at the earliest. The Vipps agreement's
    /// ceiling is raised before the notice goes out, so the first charge at the new price can go through.
    /// </summary>
    public partial class PriceChangeService(
        ApplicationDbContext db,
        IVippsService vipps,
        ISubscriptionEmailService email,
        IOptions<VippsSettings> vippsOpts,
        TimeProvider time,
        ILogger<PriceChangeService> logger) : IPriceChangeService
    {
        public async Task<(PriceChange? Change, string? Error)> ScheduleAsync(int productId, int newPriceInOre, DateTime effectiveFrom, string? adminUserId, CancellationToken ct = default)
        {
            var now = time.GetUtcNow().UtcDateTime;
            var product = await db.Products.FindAsync([productId], ct);
            if (product == null) return (null, "Product not found.");
            if (newPriceInOre <= product.PriceInOre)
                return (null, "A price change with notice is for an increase. Lower the price on the product instead, which applies at once.");
            if (effectiveFrom.Date < SubscriptionPricing.EarliestEffectiveFrom(now))
                return (null, $"The new price can take effect on {SubscriptionPricing.EarliestEffectiveFrom(now):yyyy-MM-dd} at the earliest, 30 days from now.");
            if (await db.PriceChanges.AnyAsync(c => c.ProductId == productId && c.CancelledAt == null && c.AppliedAt == null, ct))
                return (null, "This product already has a scheduled price change. Cancel it first.");

            var largestQuantity = await db.Subscriptions
                .Where(s => s.ProductId == productId && (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Pending))
                .Select(s => (int?)s.Quantity)
                .MaxAsync(ct) ?? 1;
            if ((long)newPriceInOre * largestQuantity > SubscriptionCharges.MaxAgreementAmountInOre)
                return (null, "At this price a subscriber's total would be above the 20 000 kr a Vipps agreement allows.");

            var change = new PriceChange
            {
                ProductId = productId,
                OldPriceInOre = product.PriceInOre,
                NewPriceInOre = newPriceInOre,
                EffectiveFrom = DateTime.SpecifyKind(effectiveFrom.Date, DateTimeKind.Utc),
                CreatedByUserId = adminUserId,
                CreatedAt = now,
            };
            db.PriceChanges.Add(change);
            await db.SaveChangesAsync(ct);
            LogScheduled(logger, change.Id, productId, change.OldPriceInOre, newPriceInOre, change.EffectiveFrom);
            return (change, null);
        }

        public async Task<string?> PrepareDirectEditAsync(Product product, int newPriceInOre, BillingInterval newInterval, CancellationToken ct = default)
        {
            if (newPriceInOre != product.PriceInOre
                && await db.PriceChanges.AnyAsync(c => c.ProductId == product.Id && c.CancelledAt == null && c.AppliedAt == null, ct))
                return "This product has a scheduled price change. Cancel it before editing the price.";

            var subscribed = db.Subscriptions.Where(s => s.ProductId == product.Id
                && (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Pending));
            if ((newPriceInOre > product.PriceInOre || newInterval != product.Interval) && await subscribed.AnyAsync(ct))
                return newInterval != product.Interval
                    ? "The billing interval cannot change while the product has subscribers."
                    : "The price cannot go up at once while the product has subscribers. Schedule a price change with notice instead.";

            if (newPriceInOre < product.PriceInOre)
            {
                var now = time.GetUtcNow().UtcDateTime;
                foreach (var sub in await db.Subscriptions.Where(s => s.ProductId == product.Id && s.UnitPriceInOre > newPriceInOre).ToListAsync(ct))
                {
                    sub.UnitPriceInOre = newPriceInOre;
                    sub.UpdatedAt = now;
                }
                // An increase above the new price that has not reached a subscriber yet never will.
                db.PriceChangeNotices.RemoveRange(await db.PriceChangeNotices
                    .Where(n => n.Subscription!.ProductId == product.Id && n.AppliedAt == null && n.PriceChange!.NewPriceInOre > newPriceInOre)
                    .ToListAsync(ct));
                LogLowered(logger, product.Id, product.PriceInOre, newPriceInOre);
            }
            return null;
        }

        public async Task<string?> CancelAsync(int priceChangeId, CancellationToken ct = default)
        {
            var change = await db.PriceChanges.FirstOrDefaultAsync(c => c.Id == priceChangeId, ct);
            if (change == null) return "Price change not found.";
            if (change.CancelledAt != null) return "This price change is already cancelled.";
            // A subscriber's date is never before the change's own, so nobody pays the new price before it is applied.
            if (change.AppliedAt != null)
                return "This price change has taken effect and can no longer be cancelled.";

            change.CancelledAt = time.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
            LogCancelled(logger, change.Id);
            return null;
        }

        public async Task SweepAsync(CancellationToken ct = default)
        {
            await CreateNoticesAsync(ct);
            await SendNoticesAsync(ct);
            await ApplyAsync(ct);
        }

        // Every active subscriber on a lower agreed price than a scheduled change gets a notice,
        // including one who signed up after the change was scheduled. Once the product has the new
        // price, new subscribers agree to it at signup.
        private async Task CreateNoticesAsync(CancellationToken ct)
        {
            var changes = await db.PriceChanges
                .Where(c => c.CancelledAt == null && c.AppliedAt == null)
                .ToListAsync(ct);

            foreach (var change in changes)
            {
                var subscriptionIds = await db.Subscriptions
                    .Where(s => s.ProductId == change.ProductId
                                && s.Status == SubscriptionStatus.Active
                                && s.UnitPriceInOre < change.NewPriceInOre
                                && !db.PriceChangeNotices.Any(n => n.PriceChangeId == change.Id && n.SubscriptionId == s.Id))
                    .Select(s => s.Id)
                    .ToListAsync(ct);
                foreach (var id in subscriptionIds)
                    db.PriceChangeNotices.Add(new PriceChangeNotice { PriceChangeId = change.Id, SubscriptionId = id, CreatedAt = time.GetUtcNow().UtcDateTime });
            }
            await db.SaveChangesAsync(ct);
        }

        private async Task SendNoticesAsync(CancellationToken ct)
        {
            var pending = await db.PriceChangeNotices
                .Include(n => n.PriceChange)
                .Include(n => n.Subscription)
                .Where(n => n.NotifiedAt == null && n.PriceChange!.CancelledAt == null && n.Subscription!.Status == SubscriptionStatus.Active)
                .ToListAsync(ct);

            foreach (var notice in pending)
            {
                var change = notice.PriceChange!;
                var sub = notice.Subscription!;
                if (!vippsOpts.Value.IsConfigured(sub.IsTest)) continue;
                // The ceiling goes up first. Vipps does not tell the user, and only asks them to approve
                // more when a charge goes above their own maximum.
                try
                {
                    await vipps.UpdateAgreementMaxAmountAsync(
                        sub.VippsAgreementId, change.NewPriceInOre * sub.Quantity, $"price-{change.Id}-{sub.Id}-{sub.Quantity}", sub.IsTest);
                }
                catch (Exception ex)
                {
                    LogCeilingFailed(logger, ex, sub.Id, change.Id);
                    continue;
                }

                var now = time.GetUtcNow().UtcDateTime;
                var effectiveFrom = SubscriptionPricing.EffectiveFor(change, now);
                if (!await email.SendPriceChangeNoticeAsync(sub.Id, sub.UnitPriceInOre, change.NewPriceInOre, effectiveFrom))
                {
                    LogNoticeFailed(logger, sub.Id, change.Id);
                    continue;
                }

                notice.NotifiedAt = now;
                notice.EffectiveFrom = effectiveFrom;
                await db.SaveChangesAsync(ct);
                LogNotified(logger, sub.Id, change.Id, effectiveFrom);
            }
        }

        private async Task ApplyAsync(CancellationToken ct)
        {
            var today = time.GetUtcNow().UtcDateTime.Date;

            var due = await db.PriceChangeNotices
                .Include(n => n.PriceChange)
                .Include(n => n.Subscription)
                .Where(n => n.AppliedAt == null && n.EffectiveFrom != null && n.EffectiveFrom <= today && n.PriceChange!.CancelledAt == null)
                .ToListAsync(ct);
            foreach (var notice in due)
            {
                notice.Subscription!.UnitPriceInOre = notice.PriceChange!.NewPriceInOre;
                notice.Subscription.UpdatedAt = time.GetUtcNow().UtcDateTime;
                notice.AppliedAt = time.GetUtcNow().UtcDateTime;
            }

            // New subscribers pay the new price from the effective date, with no notice needed.
            var changes = await db.PriceChanges
                .Include(c => c.Product)
                .Where(c => c.AppliedAt == null && c.CancelledAt == null && c.EffectiveFrom <= today)
                .ToListAsync(ct);
            foreach (var change in changes)
            {
                change.Product!.PriceInOre = change.NewPriceInOre;
                change.AppliedAt = time.GetUtcNow().UtcDateTime;
                LogApplied(logger, change.Id, change.ProductId);
            }
            await db.SaveChangesAsync(ct);
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "Price change {ChangeId} scheduled for product {ProductId}: {OldPrice} to {NewPrice} øre from {EffectiveFrom:yyyy-MM-dd}")]
        private static partial void LogScheduled(ILogger logger, int changeId, int productId, int oldPrice, int newPrice, DateTime effectiveFrom);

        [LoggerMessage(Level = LogLevel.Information, Message = "Product {ProductId} price lowered from {OldPrice} to {NewPrice} øre, for subscribers too")]
        private static partial void LogLowered(ILogger logger, int productId, int oldPrice, int newPrice);

        [LoggerMessage(Level = LogLevel.Information, Message = "Price change {ChangeId} cancelled")]
        private static partial void LogCancelled(ILogger logger, int changeId);

        [LoggerMessage(Level = LogLevel.Error, Message = "Raising the Vipps ceiling failed for subscription {SubscriptionId}, price change {ChangeId}. Retried on the next sweep.")]
        private static partial void LogCeilingFailed(ILogger logger, Exception ex, int subscriptionId, int changeId);

        [LoggerMessage(Level = LogLevel.Error, Message = "The price change notice for subscription {SubscriptionId}, price change {ChangeId}, was not sent. Retried on the next sweep.")]
        private static partial void LogNoticeFailed(ILogger logger, int subscriptionId, int changeId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Subscription {SubscriptionId} notified of price change {ChangeId}, in effect from {EffectiveFrom:yyyy-MM-dd}")]
        private static partial void LogNotified(ILogger logger, int subscriptionId, int changeId, DateTime effectiveFrom);

        [LoggerMessage(Level = LogLevel.Information, Message = "Price change {ChangeId} applied to product {ProductId}")]
        private static partial void LogApplied(ILogger logger, int changeId, int productId);
    }
}
