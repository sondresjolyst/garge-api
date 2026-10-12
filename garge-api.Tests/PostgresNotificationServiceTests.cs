using garge_api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace garge_api.Tests;

public class PostgresNotificationServiceTests
{
    /// <summary>Replaces the database connection with a scripted list of outcomes.</summary>
    private sealed class ScriptedService(Queue<Func<Action, CancellationToken, Task>> script) : PostgresNotificationService(
        new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
        NullLogger<PostgresNotificationService>.Instance,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = "Host=unused" }).Build(),
        null!, Mock.Of<IDeviceOwnershipService>(), null!)
    {
        public int Attempts { get; private set; }

        internal override Task ListenAsync(Action connected, CancellationToken stoppingToken)
        {
            Attempts++;
            return script.Count > 0 ? script.Dequeue()(connected, stoppingToken) : Task.Delay(Timeout.Infinite, stoppingToken);
        }

        public Task Run(CancellationToken ct) => ExecuteAsync(ct);
    }

    private static Func<Action, CancellationToken, Task> Fails() => (_, _) => throw new InvalidOperationException("connection lost");
    private static Func<Action, CancellationToken, Task> ConnectsThenFails() => (connected, _) => { connected(); throw new InvalidOperationException("connection lost"); };

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(8, 16)]
    [InlineData(16, 30)]
    [InlineData(30, 30)]
    public void NextRetryDelay_DoublesUpToThirtySeconds(int previousSeconds, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), PostgresNotificationService.NextRetryDelay(TimeSpan.FromSeconds(previousSeconds)));

    [Fact]
    public async Task ALostConnection_IsOpenedAgain_WithGrowingWaits()
    {
        var service = new ScriptedService(new Queue<Func<Action, CancellationToken, Task>>([Fails(), Fails(), Fails()]));
        var waits = new List<TimeSpan>();
        service.Delay = (d, _) => { waits.Add(d); return Task.CompletedTask; };
        using var cts = new CancellationTokenSource();

        var run = service.Run(cts.Token);
        await WaitUntil(() => service.Attempts == 4);
        cts.Cancel();
        await run;

        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], waits);
    }

    [Fact]
    public async Task AConnectionThatWorked_StartsTheWaitOverAfterItIsLost()
    {
        var service = new ScriptedService(new Queue<Func<Action, CancellationToken, Task>>([Fails(), Fails(), ConnectsThenFails(), Fails()]));
        var waits = new List<TimeSpan>();
        service.Delay = (d, _) => { waits.Add(d); return Task.CompletedTask; };
        using var cts = new CancellationTokenSource();

        var run = service.Run(cts.Token);
        await WaitUntil(() => service.Attempts == 5);
        cts.Cancel();
        await run;

        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], waits);
    }

    [Fact]
    public async Task Stopping_EndsTheLoopDuringAWait()
    {
        var service = new ScriptedService(new Queue<Func<Action, CancellationToken, Task>>([Fails()]));
        using var cts = new CancellationTokenSource();
        service.Delay = (_, ct) => { cts.Cancel(); return Task.Delay(Timeout.Infinite, ct); };

        await service.Run(cts.Token).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, service.Attempts);
    }

    [Fact]
    public async Task Stopping_EndsTheLoopWhileListening()
    {
        var service = new ScriptedService(new Queue<Func<Action, CancellationToken, Task>>());
        using var cts = new CancellationTokenSource();

        var run = service.Run(cts.Token);
        await WaitUntil(() => service.Attempts == 1);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, service.Attempts);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }
}
