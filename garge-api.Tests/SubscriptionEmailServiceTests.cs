using garge_api.Models;
using garge_api.Models.Admin;
using garge_api.Models.Subscription;
using garge_api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace garge_api.Tests;

public class SubscriptionEmailServiceTests
{
    private static async Task<(SubscriptionEmailService svc, Mock<IEmailService> email, int subId)> CreateAsync(string? failureReason, string firstName = "Ada")
    {
        var db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await db.AppSettings.AddAsync(new AppSettings { Id = 1, CompanyName = "Garge" });
        await db.Users.AddAsync(new User { Id = "user-1", Email = "buyer@example.com", UserName = "buyer@example.com", FirstName = firstName, LastName = "L" });
        await db.Products.AddAsync(new Product { Id = 1, Name = "Garge Basic", PriceInOre = 29900, Interval = BillingInterval.Monthly, Type = ProductType.Primary, IsActive = true });
        var sub = new Subscription { UserId = "user-1", ProductId = 1, VippsAgreementId = "agr", Status = SubscriptionStatus.Active, LastChargeFailureReason = failureReason };
        await db.Subscriptions.AddAsync(sub);
        await db.SaveChangesAsync();

        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(sp => sp.GetService(typeof(ApplicationDbContext))).Returns(db);
        var scope = new Mock<IServiceScope>();
        scope.SetupGet(s => s.ServiceProvider).Returns(serviceProvider.Object);
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var email = new Mock<IEmailService>();
        email.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<EmailAttachment>?>()))
            .ReturnsAsync(true);
        return (new SubscriptionEmailService(scopeFactory.Object, email.Object, NullLogger<SubscriptionEmailService>.Instance), email, sub.Id);
    }

    private static string SentBody(Mock<IEmailService> email) =>
        (string)email.Invocations.Single(i => i.Method.Name == nameof(IEmailService.SendEmailAsync)).Arguments[2];

    [Fact]
    public async Task ChargeFailed_AboveTheApprovedAmount_AsksToApproveTheNewAmount()
    {
        var (svc, email, id) = await CreateAsync(SubscriptionCharges.AmountTooHigh);
        await svc.SendChargeFailedAsync(id);

        var body = SentBody(email);
        Assert.Contains("above the maximum amount you approved", body);
        Assert.DoesNotContain("update your payment method", body);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("user_action_required")]
    [InlineData("something_new")]
    public async Task ChargeFailed_ForOtherReasons_AsksToUpdateThePaymentMethod(string? reason)
    {
        var (svc, email, id) = await CreateAsync(reason);
        await svc.SendChargeFailedAsync(id);

        var body = SentBody(email);
        Assert.Contains("update your payment method", body);
        Assert.DoesNotContain("maximum amount", body);
    }

    [Fact]
    public async Task Stopped_AboveTheApprovedAmount_SaysWhy()
    {
        var (svc, email, id) = await CreateAsync(SubscriptionCharges.AmountTooHigh);
        await svc.SendStoppedForNonPaymentAsync(id);

        Assert.Contains("above the maximum amount approved", SentBody(email));
    }

    [Fact]
    public async Task Stopped_ForOtherReasons_SaysTheChargeFailed()
    {
        var (svc, email, id) = await CreateAsync("user_action_required");
        await svc.SendStoppedForNonPaymentAsync(id);

        var body = SentBody(email);
        Assert.Contains("could not charge your subscription", body);
        Assert.DoesNotContain("maximum amount", body);
    }

    [Fact]
    public async Task PriceChange_StatesBothTotalsTheDate_AndTheRightToCancel()
    {
        var (svc, email, id) = await CreateAsync(null);

        Assert.True(await svc.SendPriceChangeNoticeAsync(id, 29900, 34900, new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc)));

        var body = SentBody(email);
        Assert.Contains("NOK 299.00", body);
        Assert.Contains("NOK 349.00 / month", body);
        Assert.Contains("2026-11-10", body);
        Assert.Contains("cancel at no cost", body);
        Assert.Contains("approve it", body);
    }

    [Fact]
    public async Task PriceChange_WhenBrevoRefusesIt_ReturnsFalse()
    {
        var (svc, email, id) = await CreateAsync(null);
        email.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<EmailAttachment>?>()))
            .ReturnsAsync(false);

        Assert.False(await svc.SendPriceChangeNoticeAsync(id, 29900, 34900, DateTime.UtcNow));
    }

    [Fact]
    public async Task PriceChange_WhenSendingFails_ReturnsFalse()
    {
        var (svc, email, id) = await CreateAsync(null);
        email.Setup(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<EmailAttachment>?>()))
            .ThrowsAsync(new HttpRequestException("brevo down"));

        Assert.False(await svc.SendPriceChangeNoticeAsync(id, 29900, 34900, DateTime.UtcNow));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(SubscriptionCharges.AmountTooHigh)]
    public async Task ChargeFailed_EncodesTheNameOnce(string? reason)
    {
        var (svc, email, id) = await CreateAsync(reason, firstName: "Tom & <Jerry>");
        await svc.SendChargeFailedAsync(id);

        var body = SentBody(email);
        Assert.Contains("Hi Tom &amp; &lt;Jerry&gt;,", body);
        Assert.DoesNotContain("&amp;amp;", body);
        Assert.DoesNotContain("<Jerry>", body);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(SubscriptionCharges.AmountTooHigh)]
    public async Task Stopped_EncodesTheNameOnce(string? reason)
    {
        var (svc, email, id) = await CreateAsync(reason, firstName: "Tom & <Jerry>");
        await svc.SendStoppedForNonPaymentAsync(id);

        var body = SentBody(email);
        Assert.Contains("Hi Tom &amp; &lt;Jerry&gt;,", body);
        Assert.DoesNotContain("&amp;amp;", body);
    }
}
