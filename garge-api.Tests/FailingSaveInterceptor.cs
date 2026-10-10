using garge_api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace garge_api.Tests;

/// <summary>
/// Fails the first save that changes anything besides the processed-webhook marker, the way a dropped
/// database connection would in the middle of handling an event.
/// </summary>
public sealed class FailingSaveInterceptor : SaveChangesInterceptor
{
    private int _failuresLeft;

    public FailingSaveInterceptor(int failures = 1) => _failuresLeft = failures;

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        var changesState = eventData.Context!.ChangeTracker.Entries()
            .Any(e => e.Entity is not garge_api.Models.Webhook.ProcessedWebhookEvent
                      && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
        if (_failuresLeft > 0 && changesState)
        {
            _failuresLeft--;
            throw new DbUpdateException("Simulated database failure");
        }
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    /// <summary>Two contexts over one in-memory database: the first fails its next state change, the second does not.</summary>
    public static (ApplicationDbContext failing, ApplicationDbContext healthy) Contexts()
    {
        var name = Guid.NewGuid().ToString();
        DbContextOptions<ApplicationDbContext> Options(params IInterceptor[] interceptors) =>
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(name)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .AddInterceptors(interceptors)
                .Options;
        return (new ApplicationDbContext(Options(new FailingSaveInterceptor())), new ApplicationDbContext(Options()));
    }
}
