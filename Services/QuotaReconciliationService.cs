using garge_api.Models;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Services
{
    /// <summary>
    /// Daily sweep that brings every owner back within their sensor capacity. When a subscription is
    /// cancelled/downgraded and its paid-period grace lapses, capacity drops below the number of active
    /// owned sensors; this job auto-suspends the newest excess (keeping the oldest by claim date) so the
    /// owner stays within plan. The owner can re-pick which sensors are active afterward via the toggle.
    /// </summary>
    public class QuotaReconciliationService : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<QuotaReconciliationService> _logger;

        public QuotaReconciliationService(IServiceScopeFactory scopeFactory, ILogger<QuotaReconciliationService> logger)
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
                    var capacity = scope.ServiceProvider.GetRequiredService<ISubscriptionCapacityService>();
                    var suspended = await ReconcileAsync(db, capacity, stoppingToken);
                    if (suspended > 0)
                        _logger.LogInformation("Quota reconciliation auto-suspended {Count} over-quota sensors", suspended);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Quota reconciliation failed");
                }

                try { await Task.Delay(Interval, stoppingToken); }
                catch (TaskCanceledException) { break; }
            }
        }

        /// <summary>
        /// Auto-suspends each owner's newest over-capacity sensors. Returns the number suspended.
        /// Keeps the oldest <c>capacity</c> active sensors (by claim date); suspends the rest.
        /// </summary>
        internal static async Task<int> ReconcileAsync(ApplicationDbContext db, ISubscriptionCapacityService capacity, CancellationToken ct = default)
        {
            var ownerIds = await db.UserSensors
                .Where(us => us.IsOwner && us.SuspendedAt == null)
                .Select(us => us.UserId)
                .Distinct()
                .ToListAsync(ct);

            var now = DateTime.UtcNow;
            var totalSuspended = 0;

            foreach (var userId in ownerIds)
            {
                // Complimentary / service-account / admin roles have no capacity limit — never suspend them.
                if (await capacity.HasSubscriptionBypassAsync(userId, ct)) continue;

                var cap = await capacity.GetCapacityAsync(userId, ct);

                var activeOwned = await db.UserSensors
                    .Where(us => us.UserId == userId && us.IsOwner && us.SuspendedAt == null)
                    .OrderBy(us => us.CreatedAt)
                    .ToListAsync(ct);

                if (activeOwned.Count <= cap) continue;

                // Keep the oldest `cap`; suspend the newest excess.
                foreach (var userSensor in activeOwned.Skip(cap))
                {
                    userSensor.SuspendedAt = now;
                    totalSuspended++;
                }
                await db.SaveChangesAsync(ct);
            }

            return totalSuspended;
        }
    }
}
