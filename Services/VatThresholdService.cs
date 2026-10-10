using garge_api.Constants;
using garge_api.Helpers;
using garge_api.Models;
using garge_api.Models.Shop;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Services
{
    /// <summary>A sale made after turnover passed the VAT threshold, before VAT registration.</summary>
    public sealed record VatOwedSale(int InvoiceId, DateTime IssuedAt, int AmountInOre, int VatInOre, DateTime? CorrectedAt, int? CreditNoteId, int? ReplacementInvoiceId);

    public sealed record VatThresholdStatus(
        long TurnoverInOre,
        long OtherTurnoverInOre,
        long ThresholdInOre,
        bool VatEnabled,
        int? CrossingInvoiceId,
        DateTime? CrossedAt,
        IReadOnlyList<VatOwedSale> Owed);

    public interface IVatThresholdService
    {
        Task<VatThresholdStatus> GetStatusAsync(DateTime now, CancellationToken ct = default);

        /// <summary>
        /// Records the sale that passed the threshold, and tells the admins when turnover reaches 80 % or
        /// 90 % of it, or passes it.
        /// </summary>
        Task CheckAsync(DateTime now, CancellationToken ct = default);
    }

    /// <summary>
    /// Tracks turnover against the 50 000 kr VAT registration threshold. Registration is required once
    /// turnover over any 12 months passes the threshold. VAT is owed on the sale that passes it and on
    /// every sale after it, and for sales to consumers it is taken out of the price paid.
    /// Turnover counts invoiced sales less refunds: captured orders and captured subscription charges.
    /// Test sales are left out. Sales outside garge-api come from the setting, which covers the last
    /// 12 months, so it only counts toward windows that end in the last 12 months.
    /// </summary>
    public class VatThresholdService(
        ApplicationDbContext db,
        ISecurityNotifier notifier,
        IAppSettingsCache settingsCache,
        ILogger<VatThresholdService> logger) : IVatThresholdService
    {
        public const long ThresholdInOre = 5_000_000;
        public static readonly int[] WarnPercents = [80, 90];

        /// <summary>Below this share of the threshold, the 80 % and 90 % warnings can be sent again.</summary>
        public const int RearmPercent = 70;

        // One check at a time in this process, so a warning is sent once and the crossing is recorded once.
        private static readonly SemaphoreSlim Gate = new(1, 1);

        private sealed record Sale(int Id, DateTime IssuedAt, int AmountInOre, int VatPercentage, DateTime? CorrectedAt);

        private async Task<List<Sale>> SalesAsync(DateTime since, CancellationToken ct) =>
            await db.Invoices.AsNoTracking()
                // Credit notes and the invoices that replace corrected ones are not new sales.
                .Where(i => i.IssuedAt > since
                            && i.Kind == InvoiceKind.Invoice && i.ReplacesInvoiceId == null
                            && (i.Order == null || (!i.Order.IsTest && i.Order.Status != OrderStatus.Refunded))
                            && (i.Subscription == null || !i.Subscription.IsTest))
                .OrderBy(i => i.IssuedAt).ThenBy(i => i.Id)
                .Select(i => new Sale(
                    i.Id, i.IssuedAt,
                    i.Order == null ? i.AmountInOre : Math.Max(0, i.AmountInOre - i.Order.RefundedInOre),
                    i.VatPercentage, i.VatCorrectedAt))
                .ToListAsync(ct);

        private static bool AtOrAfter(Sale s, DateTime issuedAt, int id) =>
            s.IssuedAt > issuedAt || (s.IssuedAt == issuedAt && s.Id >= id);

        // The first sale where the 12 months up to and including it pass the threshold. The sales are
        // in order, so a running window is kept with two pointers.
        private static Sale? FindCrossing(List<Sale> sales, long other, DateTime now)
        {
            long window = 0;
            var start = 0;
            foreach (var sale in sales)
            {
                window += sale.AmountInOre;
                while (sales[start].IssuedAt <= sale.IssuedAt.AddMonths(-12))
                    window -= sales[start++].AmountInOre;
                var withOther = sale.IssuedAt > now.AddMonths(-12) ? window + other : window;
                if (withOther > ThresholdInOre) return sale;
            }
            return null;
        }

        public async Task<VatThresholdStatus> GetStatusAsync(DateTime now, CancellationToken ct = default)
        {
            var settings = await settingsCache.GetAsync();
            var other = settings.OtherTurnoverInOre;

            // Two years back covers the 12 months before every sale in the last 12 months.
            var recent = await SalesAsync(now.AddMonths(-24), ct);
            var turnover = other + recent.Where(s => s.IssuedAt > now.AddMonths(-12)).Sum(s => (long)s.AmountInOre);

            int? crossingId = settings.VatCrossingInvoiceId;
            DateTime? crossedAt = settings.VatCrossedAt;
            if (crossingId == null && FindCrossing(recent, other, now) is { } found)
                (crossingId, crossedAt) = (found.Id, found.IssuedAt);

            List<VatOwedSale> owed = [];
            if (crossingId != null)
            {
                var since = crossedAt!.Value.AddTicks(-1);
                var sales = (await SalesAsync(since, ct))
                    .Where(s => s.VatPercentage == 0 && AtOrAfter(s, crossedAt.Value, crossingId.Value))
                    .ToList();
                var ids = sales.Select(s => s.Id).ToList();
                var documents = await db.Invoices.AsNoTracking()
                    .Where(i => (i.CreditsInvoiceId != null && ids.Contains(i.CreditsInvoiceId.Value)) || (i.ReplacesInvoiceId != null && ids.Contains(i.ReplacesInvoiceId.Value)))
                    .Select(i => new { i.Id, i.CreditsInvoiceId, i.ReplacesInvoiceId })
                    .ToListAsync(ct);
                owed = sales.Select(s => new VatOwedSale(
                        s.Id, s.IssuedAt, s.AmountInOre, Pricing.Split(s.AmountInOre, Pricing.VatPercent).Vat, s.CorrectedAt,
                        documents.FirstOrDefault(d => d.CreditsInvoiceId == s.Id)?.Id,
                        documents.FirstOrDefault(d => d.ReplacesInvoiceId == s.Id)?.Id))
                    .ToList();
            }

            return new VatThresholdStatus(turnover, other, ThresholdInOre, settings.VatEnabled, crossingId, crossedAt, owed);
        }

        public async Task CheckAsync(DateTime now, CancellationToken ct = default)
        {
            await Gate.WaitAsync(ct);
            try
            {
                var settings = await db.AppSettings.FindAsync([1], ct);
                if (settings == null || settings.VatEnabled) return;

                var status = await GetStatusAsync(now, ct);
                var changed = false;
                if (settings.VatCrossingInvoiceId == null && status.CrossingInvoiceId != null)
                {
                    settings.VatCrossingInvoiceId = status.CrossingInvoiceId;
                    settings.VatCrossedAt = status.CrossedAt;
                    changed = true;
                }

                var level = status.CrossingInvoiceId != null ? 100
                    : WarnPercents.Where(p => status.TurnoverInOre * 100 >= ThresholdInOre * p).DefaultIfEmpty(0).Max();
                if (level == 0 && settings.VatThresholdWarnedPercent is > 0 and < 100
                    && status.TurnoverInOre * 100 < ThresholdInOre * RearmPercent)
                {
                    settings.VatThresholdWarnedPercent = 0;
                    changed = true;
                }

                if (level > settings.VatThresholdWarnedPercent && await NotifyAdminsAsync(level, status, ct))
                {
                    settings.VatThresholdWarnedPercent = level;
                    changed = true;
                    logger.LogWarning("VAT threshold warning sent at {Level} %, turnover {Turnover} øre", level, status.TurnoverInOre);
                }

                if (changed)
                {
                    await db.SaveChangesAsync(ct);
                    settingsCache.Invalidate();
                }
            }
            finally
            {
                Gate.Release();
            }
        }

        // True when at least one admin was reached, so a warning nobody got is tried again.
        private async Task<bool> NotifyAdminsAsync(int level, VatThresholdStatus status, CancellationToken ct)
        {
            var (title, message) = level == 100
                ? ("Register for VAT",
                   $"Turnover passed the 50 000 kr VAT threshold with invoice #{status.CrossingInvoiceId:D4} on {LocalTime.Format(status.CrossedAt!.Value, "yyyy-MM-dd")}. Register for VAT. VAT is owed on that sale and every sale after it, taken out of the price paid. Turn VAT on in the admin settings once registered.")
                : ("VAT threshold",
                   $"Turnover over the last 12 months is {MoneyFormat.Nok((int)Math.Min(status.TurnoverInOre, int.MaxValue))} kr, {level} % of the 50 000 kr VAT registration threshold.");

            var adminIds = await (
                from ur in db.UserRoles
                join r in db.Roles on ur.RoleId equals r.Id
                where r.Name == RoleNames.Admin
                select ur.UserId)
                .Distinct()
                .ToListAsync(ct);

            var reached = false;
            foreach (var adminId in adminIds)
                reached |= await notifier.NotifyUserAsync(adminId, title, message, $"garge-vat-threshold-{level}", ct);
            if (!reached)
                logger.LogError("VAT threshold warning at {Level} % reached no admin, it is tried again with the next sale", level);
            return reached;
        }
    }
}
