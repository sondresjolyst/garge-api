using garge_api.Models;
using garge_api.Models.Admin;
using Microsoft.EntityFrameworkCore;
using SjolystInnovation.AppKit.AdminStats;

namespace garge_api.Services
{
    /// <summary>
    /// The samplers behind the admin stats history. The package stores one <see cref="DailyStatSnapshot"/>
    /// per completed UTC day and answers today live, so a change later in the day still shows.
    /// </summary>
    public static class DailyStatsSamplers
    {
        /// <summary>The first day the history can start on. It guards against a stray min-value timestamp.</summary>
        public static readonly DateOnly SanityFloor = new(2020, 1, 1);

        /// <summary>Sets the samplers and lets a first backfill reach back to <see cref="SanityFloor"/>.</summary>
        /// <remarks>The days from the floor through yesterday number today minus the floor.</remarks>
        public static void Configure(AppKitDailyStatsOptions<ApplicationDbContext, DailyStatSnapshot> o)
        {
            o.LiveSampler = LiveAsync;
            o.AsOfSampler = (db, day, ct) => AsOfAsync(db, day, Today(), ct);
            o.EarliestDay = EarliestDayAsync;
            o.MaxBackfillDays = Math.Max(1, Today().DayNumber - SanityFloor.DayNumber);
        }

        private static DateOnly Today() => DateOnly.FromDateTime(TimeProvider.System.GetUtcNow().UtcDateTime);

        /// <summary>The counts right now.</summary>
        public static async Task<DailyStatSnapshot> LiveAsync(ApplicationDbContext db, CancellationToken ct) => new()
        {
            TotalUsers = await db.Users.CountAsync(u => !u.IsDeleted, ct),
            TotalSensors = await db.Sensors.CountAsync(ct),
            TotalSwitches = await db.Switches.CountAsync(ct),
            TotalAutomations = await db.AutomationRules.CountAsync(ct),
        };

        /// <summary>
        /// The counts at the end of <paramref name="day"/>. Users are soft-deleted, so their count is rebuilt
        /// from <c>CreatedAt</c> and <c>DeletedAt</c>. Sensors, switches and automations are hard-deleted, so a
        /// past day can only count the rows that still exist. Yesterday takes their live counts instead, so
        /// removals show from the latest completed day on.
        /// </summary>
        public static async Task<DailyStatSnapshot> AsOfAsync(ApplicationDbContext db, DateOnly day, DateOnly today, CancellationToken ct)
        {
            // Npgsql refuses a DateTime with an unspecified kind against timestamptz columns.
            var end = day.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var snapshot = new DailyStatSnapshot
            {
                TotalUsers = await db.Users.CountAsync(
                    u => u.CreatedAt < end && !(u.IsDeleted && u.DeletedAt != null && u.DeletedAt < end), ct),
            };

            if (day >= today.AddDays(-1))
            {
                snapshot.TotalSensors = await db.Sensors.CountAsync(ct);
                snapshot.TotalSwitches = await db.Switches.CountAsync(ct);
                snapshot.TotalAutomations = await db.AutomationRules.CountAsync(ct);
            }
            else
            {
                snapshot.TotalSensors = await db.Sensors.CountAsync(s => s.CreatedAt < end, ct);
                snapshot.TotalSwitches = await db.Switches.CountAsync(s => s.CreatedAt < end, ct);
                snapshot.TotalAutomations = await db.AutomationRules.CountAsync(a => a.CreatedAt < end, ct);
            }
            return snapshot;
        }

        /// <summary>
        /// The day of the earliest user, sensor, switch or automation, no earlier than <see cref="SanityFloor"/>.
        /// Null when nothing has been created yet.
        /// </summary>
        public static async Task<DateOnly?> EarliestDayAsync(ApplicationDbContext db, CancellationToken ct)
        {
            DateTime?[] firsts =
            [
                await db.Users.MinAsync(u => (DateTime?)u.CreatedAt, ct),
                await db.Sensors.MinAsync(s => (DateTime?)s.CreatedAt, ct),
                await db.Switches.MinAsync(s => (DateTime?)s.CreatedAt, ct),
                await db.AutomationRules.MinAsync(a => (DateTime?)a.CreatedAt, ct),
            ];
            var days = firsts.Where(d => d.HasValue).Select(d => DateOnly.FromDateTime(d!.Value)).ToList();
            if (days.Count == 0)
                return null;
            var earliest = days.Min();
            return earliest < SanityFloor ? SanityFloor : earliest;
        }
    }
}
