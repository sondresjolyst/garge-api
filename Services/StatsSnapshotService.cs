using garge_api.Models;
using garge_api.Models.Admin;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Services
{
    /// <summary>
    /// Keeps <see cref="DailyStatSnapshot"/> current: one immutable row per UTC day holding the
    /// platform totals. Past rows are never rewritten, so the admin stats history survives even after
    /// the per-user rows it was derived from are purged (post-5y retention) — the snapshots hold only
    /// aggregate counts and are anonymous, so they can be kept indefinitely.
    ///
    /// Each run appends the days missing since the last snapshot, carrying the running totals forward
    /// from that snapshot and applying each day's deltas from live data. The first ever run backfills
    /// the whole history from the earliest signup so the chart matches the previously computed series.
    /// </summary>
    public class StatsSnapshotService : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

        // Guards against a stray epoch/min-value timestamp blowing up the backfill range.
        private static readonly DateOnly SanityFloor = new(2020, 1, 1);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<StatsSnapshotService> _logger;

        public StatsSnapshotService(IServiceScopeFactory scopeFactory, ILogger<StatsSnapshotService> logger)
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
                    var added = await EnsureUpToDateAsync(db, stoppingToken);
                    if (added > 0)
                        _logger.LogInformation("Stats snapshot appended {Count} day(s)", added);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Stats snapshot update failed");
                }

                try { await Task.Delay(Interval, stoppingToken); }
                catch (TaskCanceledException) { break; }
            }
        }

        /// <summary>
        /// Appends a snapshot for every day from the day after the latest snapshot through today,
        /// computing totals from live data. On first run (no snapshots) backfills from the earliest
        /// signup. Existing rows are left untouched. Returns the number of day-rows added.
        /// </summary>
        public static async Task<int> EnsureUpToDateAsync(ApplicationDbContext db, CancellationToken ct = default)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);

            var userCreated = await CountByDayAsync(db.Users.Select(u => u.CreatedAt), ct);
            var userDeleted = await CountByDayAsync(
                db.Users.Where(u => u.IsDeleted && u.DeletedAt != null).Select(u => u.DeletedAt!.Value), ct);
            var sensorCreated = await CountByDayAsync(db.Sensors.Select(s => s.CreatedAt), ct);
            var switchCreated = await CountByDayAsync(db.Switches.Select(s => s.CreatedAt), ct);
            var automationCreated = await CountByDayAsync(db.AutomationRules.Select(a => a.CreatedAt), ct);

            var last = await db.DailyStatSnapshots
                .OrderByDescending(s => s.Date)
                .FirstOrDefaultAsync(ct);

            DateOnly from;
            int users, sensors, switches, automations;

            if (last == null)
            {
                var earliest = new[]
                {
                    MinDay(userCreated), MinDay(sensorCreated), MinDay(switchCreated), MinDay(automationCreated),
                }.Where(d => d != null).Select(d => d!.Value).DefaultIfEmpty(DateOnly.MaxValue).Min();

                if (earliest == DateOnly.MaxValue) return 0; // nothing has ever been created

                from = earliest < SanityFloor ? SanityFloor : earliest;
                users = sensors = switches = automations = 0;
            }
            else
            {
                from = last.Date.AddDays(1);
                users = last.TotalUsers;
                sensors = last.TotalSensors;
                switches = last.TotalSwitches;
                automations = last.TotalAutomations;
            }

            if (from > today) return 0; // already current

            var newRows = new List<DailyStatSnapshot>();
            for (var date = from; date <= today; date = date.AddDays(1))
            {
                users += Get(userCreated, date) - Get(userDeleted, date);
                sensors += Get(sensorCreated, date);
                switches += Get(switchCreated, date);
                automations += Get(automationCreated, date);

                newRows.Add(new DailyStatSnapshot
                {
                    Date = date,
                    TotalUsers = users,
                    TotalSensors = sensors,
                    TotalSwitches = switches,
                    TotalAutomations = automations,
                });
            }

            db.DailyStatSnapshots.AddRange(newRows);
            await db.SaveChangesAsync(ct);
            return newRows.Count;
        }

        private static async Task<Dictionary<DateOnly, int>> CountByDayAsync(IQueryable<DateTime> dates, CancellationToken ct)
        {
            var list = await dates.ToListAsync(ct);
            return list.GroupBy(d => DateOnly.FromDateTime(d.Date))
                .ToDictionary(g => g.Key, g => g.Count());
        }

        private static int Get(Dictionary<DateOnly, int> byDay, DateOnly date) =>
            byDay.TryGetValue(date, out var n) ? n : 0;

        private static DateOnly? MinDay(Dictionary<DateOnly, int> byDay) =>
            byDay.Count == 0 ? null : byDay.Keys.Min();
    }
}
