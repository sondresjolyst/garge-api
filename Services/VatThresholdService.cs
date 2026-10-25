using System.Globalization;
using garge_api.Constants;
using garge_api.Helpers;
using garge_api.Models;
using garge_api.Models.Admin;
using garge_api.Models.Shop;
using Microsoft.EntityFrameworkCore;
using SjolystInnovation.AppKit.VatThreshold;

namespace garge_api.Services
{
    /// <summary>A sale made after turnover passed the VAT threshold, before VAT registration.</summary>
    public sealed record VatOwedSale(int InvoiceId, DateTime IssuedAt, int AmountInOre, int VatInOre, DateTime? CorrectedAt, int? CreditNoteId, int? ReplacementInvoiceId);

    /// <summary>
    /// Turnover for the 12 months up to today, with each month that period overlaps, oldest first. The
    /// crossing invoice is empty when sales outside garge-api passed the threshold.
    /// </summary>
    public sealed record VatThresholdStatus(
        long TurnoverInOre,
        long ThresholdInOre,
        bool VatEnabled,
        int? CrossingInvoiceId,
        DateTime? CrossedAt,
        IReadOnlyList<VatOwedSale> Owed,
        IReadOnlyList<VatTurnoverMonth> Months);

    public interface IVatThresholdService
    {
        Task<VatThresholdStatus> GetStatusAsync(DateTime now, CancellationToken ct = default);

        /// <summary>
        /// Records the sale that passed the threshold, and tells the admins when turnover reaches 80 % or
        /// 90 % of it, or passes it.
        /// </summary>
        Task CheckAsync(DateTime now, CancellationToken ct = default);

        /// <summary>
        /// Stores the sales outside garge-api for one month and checks the threshold. Zero removes the
        /// month. Answers false, and stores nothing, for a month that has not started.
        /// </summary>
        Task<bool> SetOutsideSalesAsync(DateOnly month, long amountInOre, DateTime now, CancellationToken ct = default);
    }

    /// <summary>
    /// Tracks turnover against the 50 000 kr VAT registration threshold with the rule in
    /// SjolystInnovation.AppKit.VatThreshold. Registration is required once turnover over any 12 months
    /// passes the threshold, counted in Norwegian days. VAT is owed on the sale that passes it and on
    /// every sale after it, and for sales to consumers it is taken out of the price paid.
    /// Turnover counts invoiced sales, captured orders and captured subscription charges, from the day
    /// they were invoiced. A refund lowers it from the day of its credit note. Test sales are left out.
    /// Sales outside garge-api are entered per month and count in every 12 months the month overlaps.
    /// </summary>
    public class VatThresholdService(
        ApplicationDbContext db,
        ISecurityNotifier notifier,
        IAppSettingsCache settingsCache,
        ILogger<VatThresholdService> logger) : IVatThresholdService
    {
        public const long ThresholdInOre = VatRegistrationThreshold.LimitInOre;
        public static readonly int[] WarnPercents = [80, 90];

        /// <summary>Below this share of the threshold, the 80 % and 90 % warnings can be sent again.</summary>
        public const int RearmPercent = 70;

        // One check at a time in this process, so a warning is sent once and the crossing is recorded once.
        private static readonly SemaphoreSlim Gate = new(1, 1);

        private sealed record Sale(int Id, DateTime IssuedAt, int AmountInOre, int VatPercentage, DateTime? CorrectedAt, List<VatRefund> Refunds)
        {
            public int KeptInOre => (int)Math.Max(0, AmountInOre - Refunds.Sum(r => r.AmountInOre));

            public VatSale ToVatSale() => new(Id.ToString(CultureInfo.InvariantCulture), IssuedAt, AmountInOre, Refunds);
        }

        // Every sale, since the first 12 months that passed the threshold can lie any time back.
        private async Task<List<Sale>> SalesAsync(DateTime now, CancellationToken ct)
        {
            var invoices = await db.Invoices.AsNoTracking()
                // Credit notes and the invoices that replace corrected ones are not new sales.
                .Where(i => i.Kind == InvoiceKind.Invoice && i.ReplacesInvoiceId == null
                            && (i.Order == null || !i.Order.IsTest)
                            && (i.Subscription == null || !i.Subscription.IsTest))
                .OrderBy(i => i.IssuedAt).ThenBy(i => i.Id)
                .Select(i => new
                {
                    i.Id, i.IssuedAt, i.AmountInOre, i.VatPercentage, i.VatCorrectedAt,
                    Refunded = i.Order == null ? i.RefundedInOre : i.Order.RefundedInOre,
                    OrderUpdatedAt = i.Order == null ? (DateTime?)null : i.Order.UpdatedAt
                })
                .ToListAsync(ct);

            // A refund credit note credits the sale, or the invoice that replaced it after a VAT correction.
            var replaced = await db.Invoices.AsNoTracking()
                .Where(i => i.ReplacesInvoiceId != null)
                .Select(i => new { i.Id, Of = i.ReplacesInvoiceId!.Value })
                .ToDictionaryAsync(i => i.Id, i => i.Of, ct);
            var notes = (await db.Invoices.AsNoTracking()
                    .Where(i => i.ForRefund && i.CreditsInvoiceId != null)
                    .Select(i => new { Of = i.CreditsInvoiceId!.Value, i.IssuedAt, i.AmountInOre })
                    .ToListAsync(ct))
                .ToLookup(n => replaced.GetValueOrDefault(n.Of, n.Of), n => new VatRefund(n.IssuedAt, -(long)n.AmountInOre));

            return invoices.Select(i =>
            {
                var refunds = notes[i.Id].ToList();
                // A refund whose credit note is not made yet counts from when the order last changed, which
                // a refund does, or from now for a subscription charge, until the retry job makes the note.
                var uncredited = i.Refunded - refunds.Sum(r => r.AmountInOre);
                if (uncredited > 0) refunds.Add(new VatRefund(i.OrderUpdatedAt ?? now, uncredited));
                return new Sale(i.Id, i.IssuedAt, i.AmountInOre, i.VatPercentage, i.VatCorrectedAt, refunds);
            }).ToList();
        }

        private async Task<Dictionary<DateOnly, long>> OutsideSalesAsync(CancellationToken ct) =>
            await db.OutsideSalesMonths.AsNoTracking().ToDictionaryAsync(m => m.Month, m => m.AmountInOre, ct);

        private static bool AtOrAfter(Sale s, DateTime issuedAt, int id) =>
            s.IssuedAt > issuedAt || (s.IssuedAt == issuedAt && s.Id >= id);

        private static DateOnly MonthOf(DateTime utc)
        {
            var day = VatRegistrationThreshold.DayOf(utc, LocalTime.Zone);
            return new DateOnly(day.Year, day.Month, 1);
        }

        public async Task<VatThresholdStatus> GetStatusAsync(DateTime now, CancellationToken ct = default)
        {
            var settings = await settingsCache.GetAsync();
            var sales = await SalesAsync(now, ct);
            var outside = await OutsideSalesAsync(ct);
            var vatSales = sales.Select(s => s.ToVatSale()).ToList();
            var today = VatRegistrationThreshold.DayOf(now, LocalTime.Zone);
            var turnover = VatRegistrationThreshold.TurnoverOn(today, vatSales, outside, LocalTime.Zone);

            // A crossing once recorded stays, since VAT is owed from it.
            int? crossingId = settings.VatCrossingInvoiceId;
            DateTime? crossedAt = settings.VatCrossedAt;
            if (crossedAt == null && VatRegistrationThreshold.FindCrossing(vatSales, outside, LocalTime.Zone, today) is { } found)
            {
                crossingId = found.Sale is { } sale ? int.Parse(sale.Reference, CultureInfo.InvariantCulture) : null;
                crossedAt = found.Sale?.AtUtc ?? LocalTime.ToUtc(found.Day.ToDateTime(TimeOnly.MinValue));
            }

            List<VatOwedSale> owed = [];
            if (crossedAt is { } from)
            {
                var owing = sales
                    .Where(s => s.VatPercentage == 0 && s.KeptInOre > 0
                                && (crossingId is { } id ? AtOrAfter(s, from, id) : s.IssuedAt >= from))
                    .ToList();
                var ids = owing.Select(s => s.Id).ToList();
                var documents = await db.Invoices.AsNoTracking()
                    .Where(i => (!i.ForRefund && i.CreditsInvoiceId != null && ids.Contains(i.CreditsInvoiceId.Value)) || (i.ReplacesInvoiceId != null && ids.Contains(i.ReplacesInvoiceId.Value)))
                    .Select(i => new { i.Id, i.CreditsInvoiceId, i.ReplacesInvoiceId })
                    .ToListAsync(ct);
                owed = owing.Select(s => new VatOwedSale(
                        s.Id, s.IssuedAt, s.KeptInOre, Pricing.Split(s.KeptInOre, Pricing.VatPercent).Vat, s.CorrectedAt,
                        documents.FirstOrDefault(d => d.CreditsInvoiceId == s.Id)?.Id,
                        documents.FirstOrDefault(d => d.ReplacesInvoiceId == s.Id)?.Id))
                    .ToList();
            }

            return new VatThresholdStatus(turnover.TotalInOre, ThresholdInOre, settings.VatEnabled, crossingId, crossedAt, owed, turnover.Months);
        }

        public async Task<bool> SetOutsideSalesAsync(DateOnly month, long amountInOre, DateTime now, CancellationToken ct = default)
        {
            month = new DateOnly(month.Year, month.Month, 1);
            if (month > MonthOf(now)) return false;

            var row = await db.OutsideSalesMonths.FirstOrDefaultAsync(m => m.Month == month, ct);
            if (amountInOre == 0)
            {
                if (row != null) db.OutsideSalesMonths.Remove(row);
            }
            else if (row == null)
            {
                db.OutsideSalesMonths.Add(new OutsideSalesMonth { Month = month, AmountInOre = amountInOre, UpdatedAt = now });
            }
            else
            {
                row.AmountInOre = amountInOre;
                row.UpdatedAt = now;
            }

            await db.SaveChangesAsync(ct);
            await CheckAsync(now, ct);
            return true;
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
                if (settings.VatCrossedAt == null && status.CrossedAt != null)
                {
                    settings.VatCrossingInvoiceId = status.CrossingInvoiceId;
                    settings.VatCrossedAt = status.CrossedAt;
                    changed = true;
                }

                var level = status.CrossedAt != null ? 100
                    : VatRegistrationThreshold.WarningLevel(status.TurnoverInOre, WarnPercents);
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
                ? ("Register for VAT", status.CrossingInvoiceId is { } invoiceId
                    ? $"Turnover passed the 50 000 kr VAT threshold with invoice #{invoiceId:D4} on {LocalTime.Format(status.CrossedAt!.Value, "yyyy-MM-dd")}. Register for VAT. VAT is owed on that sale and every sale after it, taken out of the price paid. Turn VAT on in the admin settings once registered."
                    : $"Turnover passed the 50 000 kr VAT threshold with sales outside garge on {LocalTime.Format(status.CrossedAt!.Value, "yyyy-MM-dd")}. Register for VAT. VAT is owed on every sale from that day, taken out of the price paid. Turn VAT on in the admin settings once registered.")
                : ("VAT threshold",
                   $"Turnover over the last 12 months is {MoneyFormat.Nok((int)Math.Min(status.TurnoverInOre, int.MaxValue))} kr, {level} % of the 50 000 kr VAT registration threshold.");

            var reached = await notifier.NotifyAdminsAsync(db, title, message, $"garge-vat-threshold-{level}", ct);
            if (!reached)
                logger.LogError("VAT threshold warning at {Level} % reached no admin, it is tried again with the next sale", level);
            return reached;
        }
    }
}
