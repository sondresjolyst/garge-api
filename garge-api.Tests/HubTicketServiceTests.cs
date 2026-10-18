using System.Security.Claims;
using garge_api.Services;
using Microsoft.Extensions.Caching.Memory;
using SjolystInnovation.AppKit.Auth;
using Xunit;

namespace garge_api.Tests;

public class HubTicketServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A clock the test moves by hand.</summary>
    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private static (HubTicketService service, TestClock clock) Create()
    {
        var clock = new TestClock(Start);
        return (new HubTicketService(new MemoryCache(new MemoryCacheOptions()), clock), clock);
    }

    private static ClaimsPrincipal User(string id, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, id), new("email", $"{id}@example.no") };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "AuthenticationTypes.Federation", ClaimTypes.Name, ClaimTypes.Role));
    }

    [Fact]
    public void Ticket_IsOpaqueAndRandom()
    {
        var (service, _) = Create();

        var first = service.Issue(User("u-1"));
        var second = service.Issue(User("u-1"));

        Assert.NotEqual(first, second);
        Assert.DoesNotContain('.', first);
        Assert.DoesNotContain("u-1", first);
        Assert.True(first.Length >= 43);
    }

    [Fact]
    public void Ticket_RedeemsToTheUserItWasIssuedFor()
    {
        var (service, _) = Create();
        var forOla = service.Issue(User("ola", "Admin"));
        var forKari = service.Issue(User("kari"));

        var ola = service.Redeem(forOla, null)!;
        var kari = service.Redeem(forKari, null)!;

        Assert.Equal("ola", ola.GetUserId());
        Assert.True(ola.IsInRole("Admin"));
        Assert.True(ola.Identity!.IsAuthenticated);
        Assert.Equal("ola@example.no", ola.FindFirstValue("email"));
        Assert.Equal("kari", kari.GetUserId());
        Assert.False(kari.IsInRole("Admin"));
    }

    [Fact]
    public void Ticket_WorksUntilItExpires()
    {
        var (service, clock) = Create();
        var ticket = service.Issue(User("u-1"));

        clock.Advance(HubTicketService.Lifetime - TimeSpan.FromSeconds(1));
        Assert.NotNull(service.Redeem(ticket, null));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(service.Redeem(ticket, null));
    }

    [Fact]
    public void Lifetime_IsAboutAMinute()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), HubTicketService.Lifetime);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-ticket")]
    public void UnknownTicket_IsRefused(string ticket)
    {
        var (service, _) = Create();
        service.Issue(User("u-1"));

        Assert.Null(service.Redeem(ticket, null));
    }

    [Fact]
    public void Ticket_BindsToTheFirstConnection()
    {
        var (service, _) = Create();
        var ticket = service.Issue(User("u-1"));

        Assert.NotNull(service.Redeem(ticket, null));
        Assert.NotNull(service.Redeem(ticket, "conn-a"));
        Assert.NotNull(service.Redeem(ticket, "conn-a"));
        Assert.Null(service.Redeem(ticket, "conn-b"));
    }

    [Fact]
    public void BoundTicket_KeepsWorkingForItsConnection_PastTheLifetime()
    {
        var (service, clock) = Create();
        var ticket = service.Issue(User("u-1"));
        Assert.NotNull(service.Redeem(ticket, "conn-a"));

        // Long polling sends the ticket again on every poll, long after it was issued.
        for (var i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(50));
            Assert.Equal("u-1", service.Redeem(ticket, "conn-a")?.GetUserId());
        }
        Assert.Null(service.Redeem(ticket, "conn-b"));
        Assert.Null(service.Redeem(ticket, null));
    }

    [Fact]
    public void BoundTicket_ExpiresWhenItsConnectionStopsUsingIt()
    {
        var (service, clock) = Create();
        var ticket = service.Issue(User("u-1"));
        Assert.NotNull(service.Redeem(ticket, "conn-a"));

        clock.Advance(HubTicketService.BoundIdleLifetime - TimeSpan.FromSeconds(1));
        Assert.NotNull(service.Redeem(ticket, "conn-a"));
        clock.Advance(HubTicketService.BoundIdleLifetime);
        Assert.Null(service.Redeem(ticket, "conn-a"));
    }

    [Fact]
    public void UnboundTicket_CannotBindAfterItExpires()
    {
        var (service, clock) = Create();
        var ticket = service.Issue(User("u-1"));

        clock.Advance(HubTicketService.Lifetime);

        Assert.Null(service.Redeem(ticket, "conn-a"));
    }

    [Fact]
    public void Issue_NeedsAUserId()
    {
        var (service, _) = Create();

        Assert.Throws<InvalidOperationException>(() => service.Issue(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}
