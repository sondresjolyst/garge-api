using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;

namespace garge_api.Services
{
    /// <summary>
    /// Issues and redeems the short-lived tickets that a browser uses to open the device hub.
    /// </summary>
    public interface IHubTicketService
    {
        /// <summary>Issues a ticket that stands for <paramref name="user"/> on the device hub.</summary>
        string Issue(ClaimsPrincipal user);

        /// <summary>
        /// Returns the principal the ticket was issued for, or null when the ticket is unknown, has expired,
        /// or is already bound to another connection. A ticket binds to the first connection token it is
        /// used with, so the negotiate call and every request of that one connection can use it. Once bound,
        /// it keeps working for that connection while the connection stays in use, because long polling and
        /// server-sent events send it again on every request. The principal is a snapshot taken at issue.
        /// </summary>
        ClaimsPrincipal? Redeem(string ticket, string? connectionToken);
    }

    /// <summary>
    /// Keeps tickets in memory, which is enough because garge-api runs one replica.
    /// </summary>
    public sealed class HubTicketService(IMemoryCache cache, TimeProvider time) : IHubTicketService
    {
        /// <summary>How long a ticket can open a connection.</summary>
        public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

        /// <summary>
        /// How long a bound ticket stays valid after its connection last used it. It is longer than the
        /// 90 second long polling timeout and the 30 second client timeout.
        /// </summary>
        public static readonly TimeSpan BoundIdleLifetime = TimeSpan.FromMinutes(3);

        public const string AuthenticationType = "HubTicket";

        private const string KeyPrefix = "garge:hub-ticket:";

        private sealed class Entry(IReadOnlyList<Claim> claims, string? nameType, string? roleType, DateTimeOffset expiresAt)
        {
            public IReadOnlyList<Claim> Claims { get; } = claims;
            public string? NameType { get; } = nameType;
            public string? RoleType { get; } = roleType;
            public DateTimeOffset ExpiresAt { get; } = expiresAt;
            public string? ConnectionToken;
            public long LastUsedTicks;
        }

        public string Issue(ClaimsPrincipal user)
        {
            if (string.IsNullOrEmpty(user.GetUserId()))
                throw new InvalidOperationException("A hub ticket needs a signed-in user.");

            var identity = user.Identity as ClaimsIdentity;
            var ticket = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
            var expiresAt = time.GetUtcNow() + Lifetime;
            var claims = user.Claims.Select(c => new Claim(c.Type, c.Value, c.ValueType, c.Issuer, c.OriginalIssuer)).ToList();
            // The cache drops the entry after the lifetime. Redeem checks the expiry itself against the app's clock.
            cache.Set(KeyPrefix + ticket, new Entry(claims, identity?.NameClaimType, identity?.RoleClaimType, expiresAt), Lifetime);
            return ticket;
        }

        public ClaimsPrincipal? Redeem(string ticket, string? connectionToken)
        {
            if (string.IsNullOrEmpty(ticket) || !cache.TryGetValue(KeyPrefix + ticket, out Entry? entry) || entry is null)
                return null;

            var now = time.GetUtcNow();
            var bound = Volatile.Read(ref entry.ConnectionToken);
            if (bound is null)
            {
                if (now >= entry.ExpiresAt)
                {
                    cache.Remove(KeyPrefix + ticket);
                    return null;
                }
                if (!string.IsNullOrEmpty(connectionToken))
                {
                    bound = Interlocked.CompareExchange(ref entry.ConnectionToken, connectionToken, null) ?? connectionToken;
                    if (bound == connectionToken)
                    {
                        // Keep the entry while its connection keeps using it.
                        cache.Set(KeyPrefix + ticket, entry, new MemoryCacheEntryOptions { SlidingExpiration = BoundIdleLifetime });
                        Interlocked.Exchange(ref entry.LastUsedTicks, now.UtcTicks);
                    }
                }
            }
            else if (now.UtcTicks - Interlocked.Read(ref entry.LastUsedTicks) >= BoundIdleLifetime.Ticks)
            {
                cache.Remove(KeyPrefix + ticket);
                return null;
            }

            if (bound is not null)
            {
                if (bound != connectionToken)
                    return null;
                Interlocked.Exchange(ref entry.LastUsedTicks, now.UtcTicks);
            }

            return new ClaimsPrincipal(new ClaimsIdentity(entry.Claims, AuthenticationType, entry.NameType, entry.RoleType));
        }
    }
}
