using garge_api.Services;
using SjolystInnovation.AppKit.Auth;
using Xunit;

namespace garge_api.Tests;

public class RefreshTokenCleanupServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

    private static RefreshToken Token(int id, DateTime expires, DateTime? revoked = null) =>
        new() { Id = id, Token = $"t{id}", UserId = "u1", Created = Now.AddDays(-1), Expires = expires, Revoked = revoked };

    [Fact]
    public void Expired_KeepsRevokedTokensUntilTheyExpire()
    {
        var tokens = new[]
        {
            Token(1, Now.AddMonths(3), revoked: Now.AddDays(-2)),
            Token(2, Now.AddMonths(3)),
            Token(3, Now.AddSeconds(-1), revoked: Now.AddDays(-40)),
            Token(4, Now.AddSeconds(-1)),
        }.AsQueryable();

        var ids = RefreshTokenCleanupService.Expired(tokens, Now).Select(t => t.Id).ToList();

        Assert.Equal([3, 4], ids);
    }

    [Fact]
    public void Expired_KeepsATokenThatExpiresRightNow()
    {
        var tokens = new[] { Token(1, Now) }.AsQueryable();

        Assert.Empty(RefreshTokenCleanupService.Expired(tokens, Now));
    }
}
