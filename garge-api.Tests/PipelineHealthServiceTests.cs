using garge_api.Constants;
using garge_api.Models.Pipeline;
using garge_api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace garge_api.Tests;

public class PipelineHealthServiceTests : ControllerTestBase
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Lookback = TimeSpan.FromDays(8);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PipelineHealthService Build(Models.ApplicationDbContext db) =>
        new(db, NullLogger<PipelineHealthService>.Instance);

    [Fact]
    public async Task NoHeartbeatEverReceived_OpensNoGap()
    {
        var db = CreateDbContext();
        var sut = Build(db);

        var gaps = await sut.EvaluateAsync(DateTime.UtcNow, Tick, Lookback, Ct);

        Assert.Empty(gaps);
    }

    [Fact]
    public async Task HeartbeatsNinetySecondsApart_OpenNoGap()
    {
        var db = CreateDbContext();
        var sut = Build(db);
        var t0 = DateTime.UtcNow;

        await sut.RecordHeartbeatAsync(true, t0, Ct);
        Assert.Empty(await sut.EvaluateAsync(t0.AddSeconds(90), Tick, Lookback, Ct));
        await sut.RecordHeartbeatAsync(true, t0.AddSeconds(90), Ct);
        Assert.Empty(await sut.EvaluateAsync(t0.AddSeconds(200), Tick, Lookback, Ct));
    }

    [Fact]
    public async Task MissingHeartbeat_OpensGapFromLastHealthyBeat_AndClosesWhenBeatsResume()
    {
        var db = CreateDbContext();
        var sut = Build(db);
        var t0 = DateTime.UtcNow;
        await sut.RecordHeartbeatAsync(true, t0, Ct);

        var open = Assert.Single(await sut.EvaluateAsync(t0.AddMinutes(3), Tick, Lookback, Ct));
        Assert.Equal(t0, open.StartedAt);
        Assert.Null(open.EndedAt);
        Assert.Equal(SecurityMode.GapSources.Operator, open.Source);

        await sut.EvaluateAsync(t0.AddMinutes(5), Tick, Lookback, Ct);
        await sut.EvaluateAsync(t0.AddMinutes(7), Tick, Lookback, Ct);
        await sut.RecordHeartbeatAsync(true, t0.AddMinutes(8), Ct);
        var closed = Assert.Single(await sut.EvaluateAsync(t0.AddMinutes(9), Tick, Lookback, Ct));
        Assert.Equal(t0.AddMinutes(8), closed.EndedAt);
    }

    [Fact]
    public async Task MqttDisconnected_OpensGapEvenWithFreshHeartbeat()
    {
        var db = CreateDbContext();
        var sut = Build(db);
        var t0 = DateTime.UtcNow;
        await sut.RecordHeartbeatAsync(true, t0, Ct);
        await sut.RecordHeartbeatAsync(false, t0.AddMinutes(1), Ct);

        var gap = Assert.Single(await sut.EvaluateAsync(t0.AddMinutes(1).AddSeconds(10), Tick, Lookback, Ct));

        Assert.Equal(t0, gap.StartedAt);
    }

    [Fact]
    public async Task ApiDowntimeBetweenDetectorRuns_IsRecordedAsClosedGap()
    {
        var db = CreateDbContext();
        var sut = Build(db);
        var t0 = DateTime.UtcNow.AddHours(-1);
        await sut.EvaluateAsync(t0, Tick, Lookback, Ct);

        var gap = Assert.Single(await sut.EvaluateAsync(t0.AddMinutes(40), Tick, Lookback, Ct));

        Assert.Equal(SecurityMode.GapSources.Api, gap.Source);
        Assert.Equal(t0, gap.StartedAt);
        Assert.Equal(t0.AddMinutes(40), gap.EndedAt);
    }

    [Fact]
    public async Task OnlyOneOpenGapAtATime()
    {
        var db = CreateDbContext();
        var sut = Build(db);
        var t0 = DateTime.UtcNow;
        await sut.RecordHeartbeatAsync(true, t0, Ct);

        await sut.EvaluateAsync(t0.AddMinutes(3), Tick, Lookback, Ct);
        await sut.EvaluateAsync(t0.AddMinutes(5), Tick, Lookback, Ct);

        Assert.Single(db.PipelineGaps);
    }

    [Fact]
    public void EffectiveSilence_SubtractsOverlappingGapsAndOneWakePeriod()
    {
        var now = new DateTime(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);
        var reference = now.AddMinutes(-30);
        var period = TimeSpan.FromMinutes(10);

        var overlapping = new PipelineGap { StartedAt = now.AddMinutes(-20), EndedAt = now.AddMinutes(-15), Source = "operator" };
        var before = new PipelineGap { StartedAt = now.AddMinutes(-90), EndedAt = now.AddMinutes(-60), Source = "operator" };
        var ongoing = new PipelineGap { StartedAt = now.AddMinutes(-5), Source = "operator" };

        // A gap entirely before the window forgives nothing.
        Assert.Equal(TimeSpan.FromMinutes(30), SecurityAlertService.EffectiveSilence(reference, now, [before], period));
        // 30 min window, less a 5 min gap, less one 10 min wake period.
        Assert.Equal(TimeSpan.FromMinutes(15), SecurityAlertService.EffectiveSilence(reference, now, [overlapping], period));
        // Two separate 5 min gaps still earn a single wake period: 30 - 5 - 5 - 10.
        Assert.Equal(TimeSpan.FromMinutes(10), SecurityAlertService.EffectiveSilence(reference, now, [overlapping, ongoing], period));
    }

    // A flapping pipeline used to earn one wake period per gap, so a few API restarts
    // inside the lookback window exceeded the alert threshold and nothing alerted.
    [Fact]
    public void EffectiveSilence_GrantsOneWakePeriodHoweverManyGaps()
    {
        var now = new DateTime(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);
        var reference = now.AddHours(-4);
        var period = TimeSpan.FromMinutes(10);

        var gaps = new List<PipelineGap>();
        for (var i = 1; i <= 6; i++)
        {
            gaps.Add(new PipelineGap
            {
                StartedAt = now.AddMinutes(-30 * i),
                EndedAt = now.AddMinutes(-30 * i).AddMinutes(1),
                Source = "api"
            });
        }

        // Four hours quiet, six one-minute gaps, one wake period: 240 - 6 - 10.
        Assert.Equal(TimeSpan.FromMinutes(224),
            SecurityAlertService.EffectiveSilence(reference, now, gaps, period));

        // Six separate credits would have forgiven 66 min and hidden a 25 min threshold.
        Assert.True(SecurityAlertService.EffectiveSilence(reference, now, gaps, period)
            > TimeSpan.FromMinutes(25));
    }
    [Fact]
    public void EffectiveSilence_CountsOverlappingGapsOnce()
    {
        var now = new DateTime(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);
        var reference = now.AddMinutes(-60);
        var period = TimeSpan.FromMinutes(10);
        var api = new PipelineGap { StartedAt = now.AddMinutes(-40), EndedAt = now.AddMinutes(-10), Source = "api" };
        var op = new PipelineGap { StartedAt = now.AddMinutes(-45), EndedAt = now.AddMinutes(-5), Source = "operator" };

        Assert.Equal(TimeSpan.FromMinutes(10), SecurityAlertService.EffectiveSilence(reference, now, [api, op], period));
    }

    [Fact]
    public async Task ApiDowntime_DoesNotAlsoOpenAnOperatorGapOnTheSamePass()
    {
        var db = CreateDbContext();
        var sut = Build(db);
        var t0 = DateTime.UtcNow.AddHours(-1);
        await sut.RecordHeartbeatAsync(true, t0, Ct);
        await sut.EvaluateAsync(t0, Tick, Lookback, Ct);

        var gaps = await sut.EvaluateAsync(t0.AddMinutes(30), Tick, Lookback, Ct);

        var gap = Assert.Single(gaps);
        Assert.Equal(SecurityMode.GapSources.Api, gap.Source);
    }
}
