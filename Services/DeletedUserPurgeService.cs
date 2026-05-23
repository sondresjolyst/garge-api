using garge_api.Models;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Services
{
    /// <summary>
    /// Hard-deletes soft-deleted (scrubbed) users once no legal basis remains to keep them. A deleted
    /// user's row is retained only for the Norwegian Bookkeeping Act (bokføringsloven §13): accounting
    /// records — orders, invoices, subscriptions — must be kept 5 years after the end of the accounting
    /// year of the last invoice. Once that window lapses (or the user never had an invoice), the user
    /// and that whole accounting trail are removed, satisfying GDPR storage limitation (Art 5(1)(e)).
    ///
    /// The aggregate stats history is unaffected: <see cref="StatsSnapshotService"/> has already frozen
    /// those users' days into anonymous per-day snapshots, which this job leaves untouched. Snapshots
    /// are refreshed before purging as a safety net.
    /// </summary>
    public class DeletedUserPurgeService : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<DeletedUserPurgeService> _logger;

        public DeletedUserPurgeService(IServiceScopeFactory scopeFactory, ILogger<DeletedUserPurgeService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                    // Freeze stats first so a purged user's days are preserved as anonymous snapshots.
                    await StatsSnapshotService.EnsureUpToDateAsync(db, stoppingToken);

                    var purged = await PurgeAsync(db, stoppingToken);
                    if (purged > 0)
                        _logger.LogInformation("Purged {Count} soft-deleted user(s) past their retention window", purged);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Deleted-user purge failed");
                }

                try { await Task.Delay(Interval, stoppingToken); }
                catch (TaskCanceledException) { break; }
            }
        }

        /// <summary>
        /// Hard-deletes every soft-deleted user whose retention window has lapsed, along with their
        /// orders, order items, invoices, subscriptions and profile. Each user commits independently in
        /// one SaveChanges; an unexpected failure (e.g. a missing FK case) propagates to the caller's
        /// logger after the users already purged are committed. Returns the number of users purged.
        /// </summary>
        public static async Task<int> PurgeAsync(ApplicationDbContext db, CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;
            var deletedUserIds = await db.Users
                .Where(u => u.IsDeleted)
                .Select(u => u.Id)
                .ToListAsync(ct);

            var purged = 0;
            foreach (var userId in deletedUserIds)
            {
                var orderIds = await db.Orders.Where(o => o.UserId == userId).Select(o => o.Id).ToListAsync(ct);
                var subIds = await db.Subscriptions.Where(s => s.UserId == userId).Select(s => s.Id).ToListAsync(ct);

                var invoiceDates = await db.Invoices
                    .Where(i => (i.OrderId != null && orderIds.Contains(i.OrderId.Value))
                             || (i.SubscriptionId != null && subIds.Contains(i.SubscriptionId.Value)))
                    .Select(i => i.IssuedAt)
                    .ToListAsync(ct);

                if (!IsRetentionExpired(invoiceDates, now)) continue;

                await PurgeUserAsync(db, userId, orderIds, subIds, ct);
                purged++;
            }

            return purged;
        }

        /// <summary>
        /// A user may be purged once no invoice remains within bokføringsloven's 5-year window: kept
        /// through the end of the accounting year of the last invoice plus 5 years. No invoices ⇒ no
        /// accounting basis ⇒ eligible immediately.
        /// </summary>
        internal static bool IsRetentionExpired(IReadOnlyCollection<DateTime> invoiceDates, DateTime now)
        {
            if (invoiceDates.Count == 0) return true;
            var lastYear = invoiceDates.Max().Year;
            // Retained through 31 Dec of (lastYear + 5); deletable from 1 Jan of (lastYear + 6).
            return now >= new DateTime(lastYear + 6, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        }

        private static async Task PurgeUserAsync(
            ApplicationDbContext db, string userId, List<int> orderIds, List<int> subIds, CancellationToken ct)
        {
            var user = await db.Users.FindAsync([userId], ct);
            if (user == null) return;

            db.Invoices.RemoveRange(db.Invoices.Where(i =>
                (i.OrderId != null && orderIds.Contains(i.OrderId.Value)) ||
                (i.SubscriptionId != null && subIds.Contains(i.SubscriptionId.Value))));
            db.OrderItems.RemoveRange(db.OrderItems.Where(oi => orderIds.Contains(oi.OrderId)));
            db.Orders.RemoveRange(db.Orders.Where(o => o.UserId == userId));
            db.Subscriptions.RemoveRange(db.Subscriptions.Where(s => s.UserId == userId));
            db.UserProfiles.RemoveRange(db.UserProfiles.Where(p => p.Id == userId));

            // Removing the User cascades the Identity join rows (roles/claims/logins/tokens). All
            // removals commit in one SaveChanges, so the purge is atomic per user.
            db.Users.Remove(user);
            await db.SaveChangesAsync(ct);
        }
    }
}
