using garge_api.Models;
using garge_api.Models.Push;
using garge_api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace garge_api.Tests;

public class SecurityNotifierTests : ControllerTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (SecurityNotifier Sut, Mock<IWebPushService> Push, Mock<IEmailService> Email) Build(ApplicationDbContext db, bool pushDelivers)
    {
        var push = new Mock<IWebPushService>();
        push.Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pushDelivers);
        var email = new Mock<IEmailService>();
        return (new SecurityNotifier(db, push.Object, email.Object, NullLogger<SecurityNotifier>.Instance), push, email);
    }

    private static void AddProfile(ApplicationDbContext db, bool push, bool email) =>
        db.UserProfiles.Add(new UserProfile
        {
            Id = "u1",
            PushNotificationsEnabled = push,
            EmailNotificationsEnabled = email,
            User = new User { Id = "u1", UserName = "u1", Email = "u1@example.com", FirstName = "Test", LastName = "User" },
        });

    [Fact]
    public async Task PushDelivered_EmailOff_SendsNoEmail()
    {
        var db = CreateDbContext();
        AddProfile(db, push: true, email: false);
        await db.SaveChangesAsync(Ct);
        var (sut, push, email) = Build(db, pushDelivers: true);

        Assert.True(await sut.NotifyUserAsync("u1", "t", "m", "garge-security-1", Ct));
        push.Verify(p => p.SendAsync("u1", "t", "m", "garge-security-1", It.IsAny<CancellationToken>()), Times.Once);
        email.Verify(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<EmailAttachment>?>()), Times.Never);
    }

    [Fact]
    public async Task PushNotDelivered_FallsBackToEmailEvenWhenEmailIsOff()
    {
        var db = CreateDbContext();
        AddProfile(db, push: true, email: false);
        await db.SaveChangesAsync(Ct);
        var (sut, _, email) = Build(db, pushDelivers: false);

        Assert.True(await sut.NotifyUserAsync("u1", "t", "m", null, Ct));
        email.Verify(e => e.SendEmailAsync("u1@example.com", "t", It.IsAny<string>(), It.IsAny<IReadOnlyList<EmailAttachment>?>()), Times.Once);
    }

    [Fact]
    public async Task PushOff_EmailOn_SendsEmailOnly()
    {
        var db = CreateDbContext();
        AddProfile(db, push: false, email: true);
        await db.SaveChangesAsync(Ct);
        var (sut, push, email) = Build(db, pushDelivers: true);

        Assert.True(await sut.NotifyUserAsync("u1", "t", "m", null, Ct));
        push.VerifyNoOtherCalls();
        email.Verify(e => e.SendEmailAsync("u1@example.com", "t", It.IsAny<string>(), It.IsAny<IReadOnlyList<EmailAttachment>?>()), Times.Once);
    }

    [Fact]
    public async Task HasAlertChannel_PushFlagWithoutSubscription_IsNotAChannel()
    {
        var db = CreateDbContext();

        Assert.False(await SecurityNotifier.HasAlertChannelAsync(db, "u1", pushEnabled: true, emailEnabled: false, Ct));

        db.PushSubscriptions.Add(new PushSubscription { UserId = "u1", Endpoint = "https://push", P256dh = "k", Auth = "a" });
        await db.SaveChangesAsync(Ct);

        Assert.True(await SecurityNotifier.HasAlertChannelAsync(db, "u1", pushEnabled: true, emailEnabled: false, Ct));
        Assert.True(await SecurityNotifier.HasAlertChannelAsync(db, "u1", pushEnabled: false, emailEnabled: true, Ct));
    }
}
