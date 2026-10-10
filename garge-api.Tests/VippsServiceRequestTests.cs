using garge_api.Models.Shop;
using garge_api.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace garge_api.Tests;

/// <summary>Checks the request bodies VippsService sends against the ePayment API specification.</summary>
public class VippsServiceRequestTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Sent { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Sent.Add((request, body));
            var json = request.RequestUri!.AbsolutePath.EndsWith("/accesstoken/get")
                ? """{"access_token":"token","expires_in":"3600"}"""
                : """{"redirectUrl":"https://landing.vipps.no/x","reference":"garge-order-000042"}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static (VippsService Service, CapturingHandler Handler) CreateService()
    {
        var handler = new CapturingHandler();
        var opts = Options.Create(new VippsOptions
        {
            ClientId = "id", ClientSecret = "secret", MerchantSerialNumber = "123456",
            SubscriptionKey = "key", BaseUrl = "https://api.vipps.no"
        });
        var appOpts = Options.Create(new AppOptions
        {
            FrontendBaseUrl = "https://www.garge.no",
            ApiBaseUrl = "https://garge-api.prod.tumogroup.com"
        });
        var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set("vipps_test_mode", false);
        var service = new VippsService(new HttpClient(handler), opts, appOpts, cache,
            NullLogger<VippsService>.Instance, new Mock<IServiceScopeFactory>().Object);
        return (service, handler);
    }

    private static JsonElement PaymentBody(CapturingHandler handler) =>
        JsonDocument.Parse(handler.Sent.Single(s => s.Request.RequestUri!.AbsolutePath == "/epayment/v1/payments").Body).RootElement;

    [Fact]
    public async Task CreatePayment_SendsTheVatRateAsTaxRateInBasisPoints()
    {
        var (service, handler) = CreateService();
        var order = new Order { UserId = "user-1", Id = 42, TotalInOre = 74750 };
        var lines = new List<VippsOrderLine>
        {
            new() { Name = "Sensor", Id = "1", UnitPriceInOre = 37375, UnitPriceExclVatInOre = 29900, Quantity = 2, TaxPercentageBasisPoints = 2500 }
        };

        await service.CreatePaymentAsync(order, lines, "https://www.garge.no/shop/return", "4712345678", "order-42");

        var line = PaymentBody(handler).GetProperty("receipt").GetProperty("orderLines")[0];
        Assert.Equal(2500, line.GetProperty("taxRate").GetInt32());
        // taxPercentage takes 0 to 100 and is deprecated, so it must not carry the basis points.
        Assert.False(line.TryGetProperty("taxPercentage", out _));
    }

    [Fact]
    public async Task CreatePayment_OrderLinesAddUpToTheAmount()
    {
        var (service, handler) = CreateService();
        var order = new Order { UserId = "user-1", Id = 43, TotalInOre = 74750 + 4900 };
        var lines = new List<VippsOrderLine>
        {
            new() { Name = "Sensor", Id = "1", UnitPriceInOre = 37375, UnitPriceExclVatInOre = 29900, Quantity = 2, TaxPercentageBasisPoints = 2500 },
            new() { Name = "Cable", Id = "2", UnitPriceInOre = 4900, UnitPriceExclVatInOre = 3920, Quantity = 1, TaxPercentageBasisPoints = 2500 }
        };

        await service.CreatePaymentAsync(order, lines, "https://www.garge.no/shop/return", "4712345678", "order-43");

        var body = PaymentBody(handler);
        var total = body.GetProperty("receipt").GetProperty("orderLines").EnumerateArray().Sum(l => l.GetProperty("totalAmount").GetInt32());
        Assert.Equal(body.GetProperty("amount").GetProperty("value").GetInt32(), total);
        foreach (var l in body.GetProperty("receipt").GetProperty("orderLines").EnumerateArray())
        {
            Assert.Equal(l.GetProperty("totalAmount").GetInt32(),
                l.GetProperty("totalAmountExcludingTax").GetInt32() + l.GetProperty("totalTaxAmount").GetInt32());
        }
    }

    [Fact]
    public async Task CreatePayment_WithoutVat_SendsAZeroTaxRate()
    {
        var (service, handler) = CreateService();
        var order = new Order { UserId = "user-1", Id = 44, TotalInOre = 29900 };
        var lines = new List<VippsOrderLine>
        {
            new() { Name = "Sensor", Id = "1", UnitPriceInOre = 29900, UnitPriceExclVatInOre = 29900, Quantity = 1, TaxPercentageBasisPoints = 0 }
        };

        await service.CreatePaymentAsync(order, lines, "https://www.garge.no/shop/return", "4712345678", "order-44");

        var line = PaymentBody(handler).GetProperty("receipt").GetProperty("orderLines")[0];
        Assert.Equal(0, line.GetProperty("taxRate").GetInt32());
        Assert.Equal(0, line.GetProperty("totalTaxAmount").GetInt32());
    }

    private static garge_api.Models.Subscription.Product Plan(string name) => new()
    {
        Id = 1, Name = name, PriceInOre = 29900, Interval = garge_api.Models.Subscription.BillingInterval.Monthly,
        Type = garge_api.Models.Subscription.ProductType.Primary, IsActive = true
    };

    [Fact]
    public async Task CreateAgreement_SendsAtMost45CharactersOfTheProductName()
    {
        var (service, handler) = CreateService();
        await service.CreateAgreementAsync(Plan(new string('x', 60)), "user-1", "https://www.garge.no/r", "4791234567", 29900, 1, "sub-1");

        var body = JsonDocument.Parse(handler.Sent.Single(x => x.Request.RequestUri!.AbsolutePath == "/recurring/v3/agreements").Body).RootElement;
        Assert.Equal(new string('x', 45), body.GetProperty("productName").GetString());
    }

    [Theory]
    [InlineData(40_001, 50)]
    [InlineData(0, 1)]
    [InlineData(int.MaxValue, 2)]
    public async Task CreateAgreement_AmountOutsideTheVippsLimit_SendsNothing(int unitPrice, int quantity)
    {
        var (service, handler) = CreateService();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.CreateAgreementAsync(Plan("Garge"), "user-1", "https://www.garge.no/r", "4791234567", unitPrice, quantity, "sub-1"));
        Assert.DoesNotContain(handler.Sent, x => x.Request.RequestUri!.AbsolutePath.StartsWith("/recurring/"));
    }

    [Fact]
    public async Task UpdateAgreementMaxAmount_AboveTheVippsLimit_SendsNothing()
    {
        var (service, handler) = CreateService();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.UpdateAgreementMaxAmountAsync("agr_1", SubscriptionCharges.MaxAgreementAmountInOre + 1, "key"));
        Assert.DoesNotContain(handler.Sent, x => x.Request.RequestUri!.AbsolutePath.StartsWith("/recurring/"));
    }

    [Theory]
    [InlineData("1.4.2+0123456789abcdef0123456789abcdef01234567", "1.4.2")]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("garge-api-with-a-name-longer-than-thirty", "garge-api-with-a-name-longer-t")]
    public void SystemHeader_DropsBuildMetadataAndKeepsAtMost30Characters(string value, string expected)
    {
        Assert.Equal(expected, VippsService.SystemHeader(value));
    }

    [Fact]
    public async Task EveryRequest_SendsSystemHeadersOfAtMost30Characters()
    {
        var (service, handler) = CreateService();
        await service.CreateAgreementAsync(Plan("Garge"), "user-1", "https://www.garge.no/r", "4791234567", 29900, 1, "sub-1");

        foreach (var (request, _) in handler.Sent.Where(x => !x.Request.RequestUri!.AbsolutePath.EndsWith("/accesstoken/get")))
        {
            Assert.InRange(request.Headers.GetValues("Vipps-System-Name").Single().Length, 1, 30);
            Assert.InRange(request.Headers.GetValues("Vipps-System-Version").Single().Length, 1, 30);
        }
    }

    [Theory]
    [InlineData("abc", 45, "abc")]
    [InlineData("abcdef", 3, "abc")]
    [InlineData("ab\U0001F600cd", 3, "ab")]
    [InlineData("ab\U0001F600cd", 4, "ab\U0001F600")]
    public void Truncate_NeverSplitsASurrogatePair(string value, int max, string expected)
    {
        Assert.Equal(expected, VippsService.Truncate(value, max));
    }
}
