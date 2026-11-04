using garge_api.Controllers;
using garge_api.Dtos.Sensor;
using garge_api.Models;
using garge_api.Models.Sensor;
using garge_api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace garge_api.Tests;

public class BatteryHealthControllerTests : ControllerTestBase
{
    private const int SensorId = 1;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BatteryHealthController Controller(ApplicationDbContext db, BatteryHealthAnalyzerServiceTests.Harness h, string userId = "admin-1", bool isAdmin = true)
    {
        var controller = new BatteryHealthController(
            db, NullLogger<BatteryHealthController>.Instance, h.Analyzer, new BatteryHealthReadService(db, RealMapper, h.Clock));
        controller.ControllerContext = MakeControllerContext(userId, isAdmin);
        return controller;
    }

    // A final charge with a changed peak. Only a rebuild from the readings restores it.
    private static async Task<BatteryHealthAnalyzerServiceTests.Harness> WithChangedFinalCharge()
    {
        var trace = new BatteryTrace().Hold(72, 12.4f).Hold(24, 14.4f).Hold(400, 12.7f);
        var h = new BatteryHealthAnalyzerServiceTests.Harness(trace.Samples[^1].Timestamp.AddMinutes(5));
        await h.Seed(trace.Samples);
        await h.Analyze();
        await h.WithDb(async db =>
        {
            (await db.BatteryChargeEvents.SingleAsync(Ct)).PeakVoltage = 1f;
            return await db.SaveChangesAsync(Ct);
        });
        return h;
    }

    [Fact]
    public async Task ReanalyzeAll_RebuildsFromWholeHistory()
    {
        var h = await WithChangedFinalCharge();

        await h.WithDb<object?>(async db =>
        {
            var result = Assert.IsType<OkObjectResult>(await Controller(db, h).ReanalyzeAll(Ct));
            Assert.Contains("processed = 1", result.Value!.ToString());
            return null;
        });

        Assert.Equal(14.4f, Assert.Single(await h.Events()).PeakVoltage);
    }

    [Fact]
    public async Task ReanalyzeOne_RebuildsFromWholeHistory()
    {
        var h = await WithChangedFinalCharge();

        await h.WithDb<object?>(async db =>
        {
            Assert.IsType<OkObjectResult>(await Controller(db, h).ReanalyzeOne(SensorId, Ct));
            return null;
        });

        Assert.Equal(14.4f, Assert.Single(await h.Events()).PeakVoltage);
    }

    [Fact]
    public async Task ReanalyzeOne_UnknownSensor_IsNotFound()
    {
        var h = new BatteryHealthAnalyzerServiceTests.Harness(BatteryTrace.T0);
        await h.WithDb<object?>(async db =>
        {
            Assert.IsType<NotFoundObjectResult>(await Controller(db, h).ReanalyzeOne(99, Ct));
            return null;
        });
    }

    [Fact]
    public async Task GetLatest_MapsTheHealthFields()
    {
        var trace = new BatteryTrace().Hold(72, 12.4f).Hold(24, 14.4f).Ramp(600, 13f, 12.8f);
        var h = new BatteryHealthAnalyzerServiceTests.Harness(trace.Samples[^1].Timestamp.AddMinutes(5));
        await h.Seed(trace.Samples);
        await h.Analyze();

        var dto = await h.WithDb(async db =>
            Assert.IsType<BatteryHealthDto>(Assert.IsType<OkObjectResult>(await Controller(db, h).GetLatestBatteryHealth("garge_volt")).Value));

        Assert.Equal(1, dto.SensorId);
        Assert.Equal("learning", dto.Status);
        Assert.Equal(12.8f, dto.CurrentVoltage);
        Assert.NotNull(dto.RestingMedian);
        Assert.NotNull(dto.VoltageMin24h);
        Assert.NotNull(dto.LastFullChargeAt);
        Assert.Equal(14.4f, dto.LastFullChargePeak);
        Assert.NotNull(dto.SelfDischargePctPerWeek);
    }

    [Fact]
    public async Task GetLatest_NoRow_IsNull()
    {
        var h = new BatteryHealthAnalyzerServiceTests.Harness(BatteryTrace.T0);
        await h.Seed(Array.Empty<garge_api.Helpers.BatteryHealthMath.VoltageSample>());

        var value = await h.WithDb(async db =>
            Assert.IsType<OkObjectResult>(await Controller(db, h).GetLatestBatteryHealth("garge_volt")).Value);

        Assert.Null(value);
    }

    [Fact]
    public async Task GetLatest_Seller_WithNoAccessLeft_IsForbidden()
    {
        // Selling removes the membership, so the seller's period alone gives no access, as with sensor data.
        var trace = new BatteryTrace().Hold(72, 12.4f).Hold(24, 14.4f).Hold(100, 12.7f);
        var h = new BatteryHealthAnalyzerServiceTests.Harness(trace.Samples[^1].Timestamp.AddMinutes(5));
        await h.Seed(trace.Samples);
        await h.Analyze();
        await h.WithDb(async db =>
        {
            db.SensorOwnershipPeriods.Add(new SensorOwnershipPeriod { UserId = "seller", SensorId = SensorId, StartedAt = SensorOwnershipPeriod.FirstOwnerStart, EndedAt = h.Clock.Now });
            return await db.SaveChangesAsync(Ct);
        });

        await h.WithDb<object?>(async db =>
        {
            Assert.IsType<ForbidResult>(await Controller(db, h, "seller", isAdmin: false).GetLatestBatteryHealth("garge_volt"));
            Assert.IsType<ForbidResult>(await Controller(db, h, "seller", isAdmin: false).GetChargeEvents("garge_volt", null));
            return null;
        });
    }

    [Fact]
    public async Task NonAdmin_GetsTheirOwnPeriodsFromBothEndpoints()
    {
        var trace = new BatteryTrace().Hold(72, 12.4f).Hold(24, 14.4f).Hold(100, 12.7f);
        var h = new BatteryHealthAnalyzerServiceTests.Harness(trace.Samples[^1].Timestamp.AddMinutes(5));
        await h.Seed(trace.Samples);
        await h.Analyze();
        await h.WithDb(async db =>
        {
            db.SensorOwnershipPeriods.Add(new SensorOwnershipPeriod { UserId = "late", SensorId = SensorId, StartedAt = trace.Samples[^1].Timestamp });
            db.UserSensors.Add(new UserSensor { UserId = "late", SensorId = SensorId, IsOwner = true });
            return await db.SaveChangesAsync(Ct);
        });

        var latest = await h.WithDb(async db =>
            Assert.IsType<BatteryHealthDto>(Assert.IsType<OkObjectResult>(await Controller(db, h, "late", isAdmin: false).GetLatestBatteryHealth("garge_volt")).Value));
        var events = await h.WithDb(async db =>
            Assert.IsType<List<BatteryChargeEventDto>>(Assert.IsType<OkObjectResult>(await Controller(db, h, "late", isAdmin: false).GetChargeEvents("garge_volt", null)).Value));

        Assert.Null(latest.LastFullChargeAt);
        Assert.Null(latest.RestingMedian);
        Assert.Empty(events);
    }

    [Fact]
    public async Task GetLatest_FormerOwnerWhoIsAlsoAMemberAgain_SeesTheirPeriods()
    {
        var trace = new BatteryTrace().Hold(72, 12.4f).Hold(24, 14.4f).Hold(100, 12.7f);
        var h = new BatteryHealthAnalyzerServiceTests.Harness(trace.Samples[^1].Timestamp.AddMinutes(5));
        await h.Seed(trace.Samples);
        await h.Analyze();
        await h.WithDb(async db =>
        {
            db.SensorOwnershipPeriods.Add(new SensorOwnershipPeriod { UserId = "seller", SensorId = SensorId, StartedAt = SensorOwnershipPeriod.FirstOwnerStart, EndedAt = BatteryTrace.T0.AddHours(150) });
            db.SensorOwnershipPeriods.Add(new SensorOwnershipPeriod { UserId = "seller", SensorId = SensorId, StartedAt = BatteryTrace.T0.AddHours(170) });
            db.UserSensors.Add(new UserSensor { UserId = "seller", SensorId = SensorId, IsOwner = true });
            return await db.SaveChangesAsync(Ct);
        });

        var dto = await h.WithDb(async db =>
            Assert.IsType<BatteryHealthDto>(Assert.IsType<OkObjectResult>(await Controller(db, h, "seller", isAdmin: false).GetLatestBatteryHealth("garge_volt")).Value));

        Assert.NotNull(dto.LastFullChargeAt);
    }

    [Fact]
    public async Task GetEvents_MapsTheChargeFields()
    {
        var h = await WithChangedFinalCharge();

        var dtos = await h.WithDb(async db =>
            Assert.IsType<List<BatteryChargeEventDto>>(Assert.IsType<OkObjectResult>(await Controller(db, h).GetChargeEvents("garge_volt", null)).Value));

        var dto = Assert.Single(dtos);
        Assert.True(dto.IsFull);
        Assert.Equal(12.7f, dto.SettledVoltage);
        Assert.Equal(12.4f, dto.RestingAtTime);
        // The rest is long enough and flat.
        Assert.Equal(0f, dto.SelfDischargePctPerWeek);
    }
}
