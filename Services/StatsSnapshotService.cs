using garge_api.Models;
using garge_api.Models.Admin;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Services
{
    /// <summary>
    /// Keeps <see cref="DailyStatSnapshot"/> current: one immutable row per UTC day holding the
    /// platform totals. Only <b>completed</b> days are ever persisted, and a persisted row is never
    /// rewritten — so a frozen value is always computed from that day's full set of events, and the
    /// history survives even after the per-user rows it was derived from are purged (post-5y). The
    /// snapshots hold only aggregate counts and are anonymous, so they can be kept indefinitely.
    ///
    /// Today is deliberately never frozen: it is recomputed live on every read (see
    /// <see cref="GetHistoryAsync"/>). That way a signup/deletion that happens later the same day —
    /// including a sign-up-and-delete on the same day, which nets to zero — is never lost to an early,
    /// partial-day snapshot.
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
                        _logger.LogInformation("Stats snapshot froze {Count} completed day(s)", added);
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
        /// Freezes a snapshot for every <b>completed</b> day (through yesterday) that is missing,
        /// computing each day's totals from live data and carrying the running totals forward from the
        /// last snapshot. On first run (no snapshots) backfills from the earliest signup. Today is left
        /// out — it is computed live by <see cref="GetHistoryAsync"/>. Existing rows are never touched.
        /// Returns the number of day-rows frozen.
        /// </summary>
        public static async Task<int> EnsureUpToDateAsync(ApplicationDbContext db, CancellationToken ct = default)
        {
            var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);

            var userCreated = await CountByDayAsync(db.Users.Select(u => u.CreatedAt), ct);
            var userDeleted = await CountByDayAsync(
                db.Users.Where(u => u.IsDeleted && u.DeletedAt != null).Select(u => u.DeletedAt!.Value), ct);
            var sensorCreated = await CountByDayAsync(db.Sensors.Select(s => s.CreatedAt), ct);
            var switchCreated = await CountByDayAsync(db.Switches.Select(s => s.CreatedAt), ct);
            var automationCreated = await CountByDayAsync(db.AutomationRules.Select(a => a.CreatedAt), ct);

            // Sensors, switches and automations are hard-deleted, so a row's removal leaves no trace and
            // a cumulative created-count can never dip. Sample the live counts and stamp them on the
            // latest completed day, so the series reflects removals from this point forward. Older
            // backfilled days keep the cumulative (created-only) estimate — the past cannot be rebuilt.
            var sensorsLive = await db.Sensors.CountAsync(ct);
            var switchesLive = await db.Switches.CountAsync(ct);
            var automationsLive = await db.AutomationRules.CountAsync(ct);

            var last = await db.DailyStatSnapshots
                .OrderByDescending(s => s.Date)
                .FirstOrDefaultAsync(ct);

            DateOnly from;
            var running = new Totals();

            if (last == null)
            {
                var earliest = new[]
                {
                    MinDay(userCreated), MinDay(sensorCreated), MinDay(switchCreated), MinDay(automationCreated),
                }.Where(d => d != null).Select(d => d!.Value).DefaultIfEmpty(DateOnly.MaxValue).Min();

                if (earliest == DateOnly.MaxValue) return 0; // nothing has ever been created

                from = earliest < SanityFloor ? SanityFloor : earliest;
            }
            else
            {
                from = last.Date.AddDays(1);
                running = Totals.From(last);
            }

            if (from > yesterday) return 0; // no completed day to freeze yet

            var newRows = new List<DailyStatSnapshot>();
            for (var date = from; date <= yesterday; date = date.AddDays(1))
            {
                running.Apply(date, userCreated, userDeleted, sensorCreated, switchCreated, automationCreated);
                var snapshot = running.ToSnapshot(date);
                if (date == yesterday)
                {
                    // Latest completed day: use the live counts so removals are reflected. Carry these
                    // forward so the next run continues from the actual figures.
                    snapshot.TotalSensors = running.Sensors = sensorsLive;
                    snapshot.TotalSwitches = running.Switches = switchesLive;
                    snapshot.TotalAutomations = running.Automations = automationsLive;
                }
                newRows.Add(snapshot);
            }

            db.DailyStatSnapshots.AddRange(newRows);
            await db.SaveChangesAsync(ct);
            return newRows.Count;
        }

        /// <summary>
        /// Returns the full daily series for display: the frozen completed-day snapshots plus a live,
        /// non-persisted row for today computed from current data. Freezes any missing completed days
        /// first. Empty when nothing has ever been created.
        /// </summary>
        public static async Task<List<DailyStatSnapshot>> GetHistoryAsync(ApplicationDbContext db, CancellationToken ct = default)
        {
            await EnsureUpToDateAsync(db, ct);

            var snapshots = await db.DailyStatSnapshots.OrderBy(s => s.Date).ToListAsync(ct);

            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            // Kind must be UTC: these bounds are compared against timestamptz columns, and Npgsql
            // rejects a DateTime with Kind=Unspecified.
            var todayStart = today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var tomorrow = todayStart.AddDays(1);

            // Today's live row. Users carry from the last completed day plus today's signups minus
            // today's deletions (soft-delete keeps the full record). Sensors, switches and automations
            // are hard-deleted, so use their live counts directly — that is what makes them dip.
            var running = snapshots.Count > 0 ? Totals.From(snapshots[^1]) : new Totals();
            running.Users += await db.Users.CountAsync(u => u.CreatedAt >= todayStart && u.CreatedAt < tomorrow, ct);
            running.Users -= await db.Users.CountAsync(
                u => u.IsDeleted && u.DeletedAt != null && u.DeletedAt >= todayStart && u.DeletedAt < tomorrow, ct);
            running.Sensors = await db.Sensors.CountAsync(ct);
            running.Switches = await db.Switches.CountAsync(ct);
            running.Automations = await db.AutomationRules.CountAsync(ct);

            // Don't fabricate a lone zero row when nothing has ever happened.
            if (snapshots.Count == 0 && running.IsZero) return snapshots;

            snapshots.Add(running.ToSnapshot(today));
            return snapshots;
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

        /// <summary>Mutable running totals while building the cumulative series.</summary>
        private sealed class Totals
        {
            public int Users;
            public int Sensors;
            public int Switches;
            public int Automations;

            public bool IsZero => Users == 0 && Sensors == 0 && Switches == 0 && Automations == 0;

            public static Totals From(DailyStatSnapshot s) => new()
            {
                Users = s.TotalUsers, Sensors = s.TotalSensors, Switches = s.TotalSwitches, Automations = s.TotalAutomations,
            };

            public void Apply(
                DateOnly date,
                Dictionary<DateOnly, int> userCreated, Dictionary<DateOnly, int> userDeleted,
                Dictionary<DateOnly, int> sensorCreated, Dictionary<DateOnly, int> switchCreated,
                Dictionary<DateOnly, int> automationCreated)
            {
                Users += Get(userCreated, date) - Get(userDeleted, date);
                Sensors += Get(sensorCreated, date);
                Switches += Get(switchCreated, date);
                Automations += Get(automationCreated, date);
            }

            public DailyStatSnapshot ToSnapshot(DateOnly date) => new()
            {
                Date = date, TotalUsers = Users, TotalSensors = Sensors, TotalSwitches = Switches, TotalAutomations = Automations,
            };
        }
    }
}
