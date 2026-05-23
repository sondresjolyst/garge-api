using garge_api.Models;
using garge_api.Models.Admin;
using garge_api.Models.Sensor;
using garge_api.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace garge_api.Tests;

public class StatsSnapshotServiceTests
{
    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static User MakeUser(string id, DateOnly created, DateOnly? deleted = null) => new()
    {
        Id = id, UserName = id, Email = $"{id}@test.com", FirstName = "T", LastName = "U",
        CreatedAt = created.ToDateTime(new TimeOnly(12, 0)),
        IsDeleted = deleted != null,
        DeletedAt = deleted?.ToDateTime(new TimeOnly(12, 0)),
    };

    private static Sensor MakeSensor(int id, DateOnly created) => new()
    {
        Id = id, Name = $"s{id}", Type = "voltage", Role = "sensor",
        RegistrationCode = $"rc{id}", DefaultName = "D", ParentName = "gw",
        CreatedAt = created.ToDateTime(new TimeOnly(12, 0)),
    };

    private static DailyStatSnapshot? Frozen(ApplicationDbContext db, DateOnly date) =>
        db.DailyStatSnapshots.SingleOrDefault(s => s.Date == date);

    private static int UsersOn(List<DailyStatSnapshot> series, DateOnly date) =>
        series.Single(s => s.Date == date).TotalUsers;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task History_ClimbsOnSignup_DipsOnDeletion()
    {
        using var db = NewDb();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var d1 = today.AddDays(-3);
        var d2 = today.AddDays(-2);
        db.Users.AddRange(
            MakeUser("u1", d1),
            MakeUser("u2", d1),
            MakeUser("u3", d1, deleted: d2));
        await db.SaveChangesAsync(Ct);

        var series = await StatsSnapshotService.GetHistoryAsync(db, Ct);

        Assert.Equal(3, UsersOn(series, d1));
        Assert.Equal(2, UsersOn(series, d2));
        Assert.Equal(2, UsersOn(series, today));
    }

    [Fact]
    public async Task EnsureUpToDate_FreezesCompletedDaysOnly_NotToday()
    {
        using var db = NewDb();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        db.Users.Add(MakeUser("u1", today.AddDays(-2)));
        db.Users.Add(MakeUser("u2", today)); // signed up today
        await db.SaveChangesAsync(Ct);

        await StatsSnapshotService.EnsureUpToDateAsync(db, Ct);

        Assert.NotNull(Frozen(db, today.AddDays(-1)));   // yesterday frozen
        Assert.Null(Frozen(db, today));                  // today never frozen
        Assert.Equal(1, Frozen(db, today.AddDays(-1))!.TotalUsers);
    }

    [Fact]
    public async Task SameDaySignupAndDelete_NetsToZero_OnThatDay()
    {
        // The reported edge: a user signs up and deletes on the same day. They must net to zero — never
        // bumping the count for that day — whether the day is still live or already frozen.
        using var db = NewDb();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var pastDay = today.AddDays(-3);
        db.Users.Add(MakeUser("baseline", today.AddDays(-5)));
        db.Users.Add(MakeUser("churned-live", today, deleted: today));       // same-day, today (live)
        db.Users.Add(MakeUser("churned-past", pastDay, deleted: pastDay));   // same-day, completed day
        await db.SaveChangesAsync(Ct);

        var series = await StatsSnapshotService.GetHistoryAsync(db, Ct);

        Assert.Equal(1, UsersOn(series, pastDay)); // only baseline; the past same-day churn nets out
        Assert.Equal(1, UsersOn(series, today));   // baseline only; the live same-day churn nets out
        Assert.Equal(1, Frozen(db, pastDay)!.TotalUsers); // frozen value also nets out
    }

    [Fact]
    public async Task TodayIsNeverFrozen_SoLaterSameDayActivityIsReflected()
    {
        using var db = NewDb();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        db.Users.Add(MakeUser("u1", today.AddDays(-1)));
        await db.SaveChangesAsync(Ct);

        var first = await StatsSnapshotService.GetHistoryAsync(db, Ct);
        Assert.Equal(1, UsersOn(first, today));

        // A second signup happens later the same day.
        db.Users.Add(MakeUser("u2", today));
        await db.SaveChangesAsync(Ct);

        var second = await StatsSnapshotService.GetHistoryAsync(db, Ct);
        Assert.Equal(2, UsersOn(second, today)); // reflected, not lost to a frozen partial-day row
    }

    [Fact]
    public async Task ExistingSnapshots_AreFrozen_OnlyLaterDaysAppended()
    {
        // A frozen old snapshot whose totals no longer match live data (its source rows were purged)
        // must be left untouched; only later completed days are appended, carrying it forward.
        using var db = NewDb();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var old = today.AddDays(-10);
        db.DailyStatSnapshots.Add(new DailyStatSnapshot
        {
            Date = old, TotalUsers = 500, TotalSensors = 10, TotalSwitches = 2, TotalAutomations = 1,
        });
        db.Users.Add(MakeUser("u1", today.AddDays(-1))); // a recent completed-day signup
        await db.SaveChangesAsync(Ct);

        var added = await StatsSnapshotService.EnsureUpToDateAsync(db, Ct);

        Assert.Equal(9, added);                          // old+1 .. yesterday
        Assert.Equal(500, Frozen(db, old)!.TotalUsers);  // frozen, not recomputed
        Assert.Equal(501, Frozen(db, today.AddDays(-1))!.TotalUsers); // carried base + the signup
        Assert.Null(Frozen(db, old.AddDays(-1)));        // nothing before the frozen row
    }

    [Fact]
    public async Task SecondRun_AppendsNothing()
    {
        using var db = NewDb();
        db.Users.Add(MakeUser("u1", DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-2)));
        await db.SaveChangesAsync(Ct);

        await StatsSnapshotService.EnsureUpToDateAsync(db, Ct);
        var count = db.DailyStatSnapshots.Count();

        var second = await StatsSnapshotService.EnsureUpToDateAsync(db, Ct);

        Assert.Equal(0, second);
        Assert.Equal(count, db.DailyStatSnapshots.Count());
    }

    [Fact]
    public async Task Sensors_UseLiveCount_AndDipWhenRemoved()
    {
        // Sensors are hard-deleted, so the series tracks the live count: today reflects removals, and
        // the latest frozen day is stamped with the live count too.
        using var db = NewDb();
        var twoDaysAgo = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-2);
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        db.Sensors.AddRange(MakeSensor(1, twoDaysAgo), MakeSensor(2, twoDaysAgo), MakeSensor(3, twoDaysAgo));
        await db.SaveChangesAsync(Ct);

        var before = await StatsSnapshotService.GetHistoryAsync(db, Ct);
        Assert.Equal(3, before[^1].TotalSensors);              // today = live count
        Assert.Equal(3, Frozen(db, yesterday)!.TotalSensors);  // latest frozen day stamped live

        db.Sensors.Remove(db.Sensors.Single(s => s.Id == 3));  // hard delete
        await db.SaveChangesAsync(Ct);

        var after = await StatsSnapshotService.GetHistoryAsync(db, Ct);
        Assert.Equal(2, after[^1].TotalSensors);               // today dips
        Assert.Equal(3, Frozen(db, yesterday)!.TotalSensors);  // frozen day unchanged
    }

    [Fact]
    public async Task NoActivity_AddsNothing_AndEmptyHistory()
    {
        using var db = NewDb();
        Assert.Equal(0, await StatsSnapshotService.EnsureUpToDateAsync(db, Ct));
        Assert.Empty(await StatsSnapshotService.GetHistoryAsync(db, Ct));
    }
}
