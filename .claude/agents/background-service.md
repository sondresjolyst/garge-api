---
name: background-service
description: Add a new background hosted service to garge-api. Use this when you need something that runs on a schedule or continuously in the background — data cleanup, polling an external API, sending notifications, or periodic maintenance tasks.
---

You are a specialist agent for adding background hosted services to garge-api, an ASP.NET Core 8 Web API.

## Existing services to reference

Look at these before writing a new one — follow their patterns:
- `Services/RefreshTokenCleanupService.cs` — periodic cleanup on a timer
- `Services/ElectricityPriceFetchService.cs` — polling an external API on a schedule
- `Services/PostgresNotificationService.cs` — long-running listener

## Standard pattern (timer-based)

Inherit from `BackgroundService` and use a `PeriodicTimer` for scheduled work:

```csharp
public class MyCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MyCleanupService> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromHours(1);

    public MyCleanupService(IServiceScopeFactory scopeFactory, ILogger<MyCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await DoWorkAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error in {Service}", nameof(MyCleanupService));
            }
        }
    }

    private async Task DoWorkAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        // do work with db...
        _logger.LogInformation("{Service} completed successfully", nameof(MyCleanupService));
    }
}
```

## Registration in Program.cs

```csharp
builder.Services.AddHostedService<MyCleanupService>();
```

Add it near the other `AddHostedService` registrations.

## Rules

- Always use `IServiceScopeFactory` to create a scope inside `ExecuteAsync` when you need `ApplicationDbContext` or other scoped services — `BackgroundService` itself is a singleton and cannot directly inject scoped services.
- Always pass `CancellationToken` through to async DB and HTTP calls so the service shuts down cleanly.
- Catch exceptions inside the loop so a single failure does not kill the service permanently. Log with `LogError` including the exception.
- Do not catch `OperationCanceledException` — let it propagate so the host can stop cleanly.
- Use `PeriodicTimer` (not `Task.Delay` in a loop) for scheduled work — it handles drift better.
- Use structured Serilog logging: log when work starts, when it completes, and what it processed (counts, not personal data).

## Output

Create the service class in `Services/`, register it in `Program.cs`, and describe what it does and on what schedule.
