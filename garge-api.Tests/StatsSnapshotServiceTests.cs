using garge_api.Models;
using garge_api.Models.Admin;
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
        CreatedAt = created.ToDateTime(TimeOnly.MinValue),
        IsDeleted = deleted != null,
        DeletedAt = deleted?.ToDateTime(TimeOnly.MinValue),
    };

    private static DailyStatSnapshot? Day(ApplicationDbContext db, DateOnly date) =>
        db.DailyStatSnapshots.SingleOrDefault(s => s.Date == date);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Backfill_ClimbsOnSignup_DipsOnDeletion()
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

        var added = await StatsSnapshotService.EnsureUpToDateAsync(db, Ct);

        Assert.Equal(4, added); // d1 .. today inclusive
        Assert.Equal(3, Day(db, d1)!.TotalUsers);
        Assert.Equal(2, Day(db, d2)!.TotalUsers);
        Assert.Equal(2, Day(db, today)!.TotalUsers);
    }

    [Fact]
    public async Task SecondRun_SameDay_AppendsNothing()
    {
        using var db = NewDb();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        db.Users.Add(MakeUser("u1", today.AddDays(-1)));
        await db.SaveChangesAsync(Ct);

        await StatsSnapshotService.EnsureUpToDateAsync(db, Ct);
        var countAfterFirst = db.DailyStatSnapshots.Count();

        var second = await StatsSnapshotService.EnsureUpToDateAsync(db, Ct);

        Assert.Equal(0, second);
        Assert.Equal(countAfterFirst, db.DailyStatSnapshots.Count());
    }

    [Fact]
    public async Task ExistingSnapshots_AreFrozen_OnlyLaterDaysAppended()
    {
        // A frozen old snapshot whose totals no longer match live data (e.g. its source rows were
        // purged) must be left untouched; only days after it are appended, carrying its totals forward.
        using var db = NewDb();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var old = today.AddDays(-10);
        db.DailyStatSnapshots.Add(new DailyStatSnapshot
        {
            Date = old, TotalUsers = 500, TotalSensors = 10, TotalSwitches = 2, TotalAutomations = 1,
        });
        db.Users.Add(MakeUser("u1", today)); // one recent signup; the 500 historical users are "purged"
        await db.SaveChangesAsync(Ct);

        var added = await StatsSnapshotService.EnsureUpToDateAsync(db, Ct);

        Assert.Equal(10, added);                       // old+1 .. today
        Assert.Equal(500, Day(db, old)!.TotalUsers);   // frozen, not recomputed
        Assert.Equal(501, Day(db, today)!.TotalUsers); // carried base + today's signup
        Assert.Null(Day(db, old.AddDays(-1)));         // nothing before the frozen row
    }

    [Fact]
    public async Task NoActivity_AddsNothing()
    {
        using var db = NewDb();
        var added = await StatsSnapshotService.EnsureUpToDateAsync(db, Ct);
        Assert.Equal(0, added);
        Assert.Empty(db.DailyStatSnapshots);
    }
}
