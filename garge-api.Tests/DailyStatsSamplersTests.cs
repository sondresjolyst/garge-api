using garge_api.Models;
using garge_api.Models.Admin;
using garge_api.Models.Sensor;
using garge_api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SjolystInnovation.AppKit.AdminStats;
using Xunit;

namespace garge_api.Tests;

public class DailyStatsSamplersTests
{
    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static AppKitDailyStats<ApplicationDbContext, DailyStatSnapshot> NewStats()
    {
        var options = new AppKitDailyStatsOptions<ApplicationDbContext, DailyStatSnapshot>();
        DailyStatsSamplers.Configure(options);
        return new(Options.Create(options), TimeProvider.System);
    }

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
        db.DailyStatSnapshots.AsNoTracking().SingleOrDefault(s => s.Date == date);

    private static int UsersOn(List<DailyStatSnapshot> series, DateOnly date) =>
        series.Single(s => s.Date == date).TotalUsers;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task History_ClimbsOnSignup_DipsOnDeletion()
    {
        using var db = NewDb();
        var stats = NewStats();
        var d1 = Today.AddDays(-3);
        var d2 = Today.AddDays(-2);
        db.Users.AddRange(
            MakeUser("u1", d1),
            MakeUser("u2", d1),
            MakeUser("u3", d1, deleted: d2));
        await db.SaveChangesAsync(Ct);

        await stats.FreezeMissingDaysAsync(db, Ct);
        var series = await stats.GetHistoryAsync(db, cancellationToken: Ct);

        Assert.Equal(3, UsersOn(series, d1));
        Assert.Equal(2, UsersOn(series, d2));
        Assert.Equal(2, UsersOn(series, Today));
    }

    [Fact]
    public async Task Freeze_StoresCompletedDaysOnly_NotToday()
    {
        using var db = NewDb();
        db.Users.Add(MakeUser("u1", Today.AddDays(-2)));
        db.Users.Add(MakeUser("u2", Today)); // signed up today
        await db.SaveChangesAsync(Ct);

        await NewStats().FreezeMissingDaysAsync(db, Ct);

        Assert.Equal(1, Frozen(db, Today.AddDays(-2))!.TotalUsers);
        Assert.Equal(1, Frozen(db, Today.AddDays(-1))!.TotalUsers);
        Assert.Null(Frozen(db, Today));
    }

    [Fact]
    public async Task SameDaySignupAndDelete_NetsToZero_OnThatDay()
    {
        // A user who signs up and deletes on the same day never bumps that day's count, whether the day
        // is still live or already stored.
        using var db = NewDb();
        var stats = NewStats();
        var pastDay = Today.AddDays(-3);
        db.Users.Add(MakeUser("baseline", Today.AddDays(-5)));
        db.Users.Add(MakeUser("churned-live", Today, deleted: Today));
        db.Users.Add(MakeUser("churned-past", pastDay, deleted: pastDay));
        await db.SaveChangesAsync(Ct);

        await stats.FreezeMissingDaysAsync(db, Ct);
        var series = await stats.GetHistoryAsync(db, cancellationToken: Ct);

        Assert.Equal(1, UsersOn(series, pastDay));
        Assert.Equal(1, UsersOn(series, Today));
        Assert.Equal(1, Frozen(db, pastDay)!.TotalUsers);
    }

    [Fact]
    public async Task TodayIsNeverFrozen_SoLaterSameDayActivityIsReflected()
    {
        using var db = NewDb();
        var stats = NewStats();
        db.Users.Add(MakeUser("u1", Today.AddDays(-1)));
        await db.SaveChangesAsync(Ct);

        await stats.FreezeMissingDaysAsync(db, Ct);
        Assert.Equal(1, UsersOn(await stats.GetHistoryAsync(db, cancellationToken: Ct), Today));

        db.Users.Add(MakeUser("u2", Today));
        await db.SaveChangesAsync(Ct);
        await stats.FreezeMissingDaysAsync(db, Ct);

        Assert.Equal(2, UsersOn(await stats.GetHistoryAsync(db, cancellationToken: Ct), Today));
        Assert.Null(Frozen(db, Today));
    }

    [Fact]
    public async Task ExistingSnapshots_AreLeftAlone_OnlyLaterDaysAppended()
    {
        // A stored snapshot whose totals no longer match live data, because its source rows were purged,
        // stays as it is. Only later completed days are added, each rebuilt from the data for that day.
        using var db = NewDb();
        var old = Today.AddDays(-10);
        db.DailyStatSnapshots.Add(new DailyStatSnapshot
        {
            Date = old, TotalUsers = 500, TotalSensors = 10, TotalSwitches = 2, TotalAutomations = 1,
        });
        db.Users.Add(MakeUser("u1", Today.AddDays(-1)));
        await db.SaveChangesAsync(Ct);

        var added = await NewStats().FreezeMissingDaysAsync(db, Ct);

        Assert.Equal(9, added); // old+1 .. yesterday
        Assert.Equal(500, Frozen(db, old)!.TotalUsers);
        Assert.Equal(0, Frozen(db, Today.AddDays(-2))!.TotalUsers);
        Assert.Equal(1, Frozen(db, Today.AddDays(-1))!.TotalUsers);
        Assert.Null(Frozen(db, old.AddDays(-1)));
    }

    [Fact]
    public async Task SecondRun_AppendsNothing()
    {
        using var db = NewDb();
        var stats = NewStats();
        db.Users.Add(MakeUser("u1", Today.AddDays(-2)));
        await db.SaveChangesAsync(Ct);

        await stats.FreezeMissingDaysAsync(db, Ct);
        var count = db.DailyStatSnapshots.Count();

        Assert.Equal(0, await stats.FreezeMissingDaysAsync(db, Ct));
        Assert.Equal(count, db.DailyStatSnapshots.Count());
    }

    [Fact]
    public async Task Sensors_UseLiveCount_AndDipWhenRemoved()
    {
        // Sensors are hard-deleted, so today and the latest stored day take the live count, and today dips
        // when one is removed.
        using var db = NewDb();
        var stats = NewStats();
        var twoDaysAgo = Today.AddDays(-2);
        var yesterday = Today.AddDays(-1);
        db.Sensors.AddRange(MakeSensor(1, twoDaysAgo), MakeSensor(2, twoDaysAgo), MakeSensor(3, twoDaysAgo));
        await db.SaveChangesAsync(Ct);

        await stats.FreezeMissingDaysAsync(db, Ct);
        var before = await stats.GetHistoryAsync(db, cancellationToken: Ct);
        Assert.Equal(3, before[^1].TotalSensors);
        Assert.Equal(3, Frozen(db, yesterday)!.TotalSensors);

        db.Sensors.Remove(db.Sensors.Single(s => s.Id == 3));
        await db.SaveChangesAsync(Ct);

        var after = await stats.GetHistoryAsync(db, cancellationToken: Ct);
        Assert.Equal(2, after[^1].TotalSensors);
        Assert.Equal(3, Frozen(db, yesterday)!.TotalSensors);
    }

    [Fact]
    public async Task AsOf_PastDayCountsRowsCreatedBeforeItsEnd_YesterdayTakesLiveCounts()
    {
        using var db = NewDb();
        db.Sensors.AddRange(MakeSensor(1, Today.AddDays(-5)), MakeSensor(2, Today.AddDays(-3)));
        db.Users.AddRange(MakeUser("u1", Today.AddDays(-5)), MakeUser("u2", Today.AddDays(-3), deleted: Today.AddDays(-1)));
        await db.SaveChangesAsync(Ct);

        var past = await DailyStatsSamplers.AsOfAsync(db, Today.AddDays(-4), Today, Ct);
        Assert.Equal(1, past.TotalSensors);
        Assert.Equal(1, past.TotalUsers);

        var beforeDeletion = await DailyStatsSamplers.AsOfAsync(db, Today.AddDays(-2), Today, Ct);
        Assert.Equal(2, beforeDeletion.TotalUsers);

        db.Sensors.Add(MakeSensor(3, Today)); // created today, after yesterday ended
        await db.SaveChangesAsync(Ct);

        var yesterday = await DailyStatsSamplers.AsOfAsync(db, Today.AddDays(-1), Today, Ct);
        Assert.Equal(3, yesterday.TotalSensors); // live
        Assert.Equal(1, yesterday.TotalUsers);   // rebuilt, u2 deleted that day
    }

    [Fact]
    public async Task FirstRun_BackfillsFromTheEarliestRecordAcrossTables()
    {
        using var db = NewDb();
        db.Users.Add(MakeUser("u1", Today.AddDays(-5)));
        db.Sensors.Add(MakeSensor(1, Today.AddDays(-30)));
        await db.SaveChangesAsync(Ct);

        Assert.Equal(Today.AddDays(-30), await DailyStatsSamplers.EarliestDayAsync(db, Ct));
        Assert.Equal(30, await NewStats().FreezeMissingDaysAsync(db, Ct));
        Assert.Equal(1, Frozen(db, Today.AddDays(-30))!.TotalSensors);
        Assert.Equal(0, Frozen(db, Today.AddDays(-30))!.TotalUsers);
    }

    [Fact]
    public async Task EarliestDay_ClampsAStrayTimestampToTheSanityFloor()
    {
        using var db = NewDb();
        db.Users.Add(MakeUser("stray", DateOnly.MinValue));
        await db.SaveChangesAsync(Ct);

        Assert.Equal(DailyStatsSamplers.SanityFloor, await DailyStatsSamplers.EarliestDayAsync(db, Ct));
    }

    [Fact]
    public void Configure_LetsTheFirstBackfillReachTheSanityFloor()
    {
        var options = new AppKitDailyStatsOptions<ApplicationDbContext, DailyStatSnapshot>();
        DailyStatsSamplers.Configure(options);

        // The package stores at most MaxBackfillDays days, ending yesterday.
        Assert.Equal(DailyStatsSamplers.SanityFloor, Today.AddDays(-1).AddDays(1 - options.MaxBackfillDays));
        Assert.NotNull(options.LiveSampler);
        Assert.NotNull(options.AsOfSampler);
        Assert.NotNull(options.EarliestDay);
    }

    [Fact]
    public async Task NoActivity_AddsNothing_AndEmptyHistory()
    {
        using var db = NewDb();
        var stats = NewStats();

        Assert.Null(await DailyStatsSamplers.EarliestDayAsync(db, Ct));
        Assert.Equal(0, await stats.FreezeMissingDaysAsync(db, Ct));
        Assert.Empty(await stats.GetHistoryAsync(db, cancellationToken: Ct));
    }
}
