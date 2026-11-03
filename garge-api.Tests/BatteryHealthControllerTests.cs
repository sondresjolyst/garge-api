using garge_api.Controllers;
using garge_api.Dtos.Sensor;
using garge_api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace garge_api.Tests;

public class BatteryHealthControllerTests : ControllerTestBase
{
    private const int SensorId = 1;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BatteryHealthController Controller(ApplicationDbContext db, BatteryHealthAnalyzerServiceTests.Harness h)
    {
        var controller = new BatteryHealthController(db, RealMapper, NullLogger<BatteryHealthController>.Instance, h.Analyzer);
        controller.ControllerContext = MakeControllerContext("admin-1", isAdmin: true);
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
        var h = await WithChangedFinalCharge();
        await h.WithDb(async db =>
        {
            db.BatteryHealthData.Add(new Models.Sensor.BatteryHealth { SensorId = SensorId, Status = "good", RestingMedian = null, SelfDischargePctPerWeek = 1.5f, Timestamp = h.Clock.Now.AddDays(1) });
            return await db.SaveChangesAsync(Ct);
        });

        var dto = await h.WithDb(async db =>
            Assert.IsType<BatteryHealthDto>(Assert.IsType<OkObjectResult>(await Controller(db, h).GetLatestBatteryHealth("garge_volt")).Value));

        Assert.Null(dto.RestingMedian);
        Assert.Equal(1.5f, dto.SelfDischargePctPerWeek);
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
    }
}
