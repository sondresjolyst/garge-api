using garge_api.Controllers;
using garge_api.Dtos.Shop;
using Microsoft.AspNetCore.Http;
using garge_api.Models;
using garge_api.Models.Admin;
using garge_api.Models.Shop;
using garge_api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace garge_api.Tests;

public class ShopControllerTests : ControllerTestBase
{
    private static Mock<IInvoiceService> MockInvoice() => new();

    private static Mock<IVippsService> MockVipps()
    {
        var mock = new Mock<IVippsService>();
        mock.Setup(v => v.VerifyWebhookSignature(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string body, string sig, string secret) =>
            {
                if (string.IsNullOrEmpty(secret) || !sig.StartsWith("sha256=")) return false;
                var computed = "sha256=" + Convert.ToHexString(
                    HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))
                ).ToLowerInvariant();
                return CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(computed),
                    Encoding.ASCII.GetBytes(sig.ToLowerInvariant()));
            });
        return mock;
    }

    private ShopController CreateController(
        ApplicationDbContext db, string userId = "user-1",
        IVippsService? vipps = null, IInvoiceService? invoice = null)
    {
        var ctrl = new ShopController(
            db, vipps ?? MockVipps().Object,
            invoice ?? MockInvoice().Object,
            MockMapper.Object,
            NullLogger<ShopController>.Instance);
        ctrl.ControllerContext = MakeControllerContext(userId);
        return ctrl;
    }

    [Fact]
    public async Task Webhook_ValidHmac_AuthorizedEvent_SetsReserved()
    {
        using var db = CreateDbContext();
        await db.AppSettings.AddAsync(new AppSettings { Id = 1, VippsShopWebhookSecret = "secret" });
        var order = new Order
        {
            UserId = "user-1", VippsOrderId = "vipps-ref",
            TotalInOre = 10000, Status = OrderStatus.Pending
        };
        await db.Orders.AddAsync(order);
        await db.SaveChangesAsync();

        var payload = new { reference = order.Id.ToString(), name = "AUTHORIZED" };
        var body = JsonSerializer.Serialize(payload);
        var sig = BuildSig(body, "secret");

        var ctrl = CreateController(db);
        SetupWebhookRequest(ctrl, body, sig);

        var result = await ctrl.Webhook();

        Assert.IsType<OkResult>(result);
        var updated = await db.Orders.FindAsync(order.Id);
        Assert.Equal(OrderStatus.Reserved, updated!.Status);
    }

    [Fact]
    public async Task Webhook_InvalidHmac_Returns401()
    {
        using var db = CreateDbContext();
        await db.AppSettings.AddAsync(new AppSettings { Id = 1, VippsShopWebhookSecret = "secret" });
        await db.SaveChangesAsync();

        var body = """{"reference":"1","name":"AUTHORIZED"}""";
        var ctrl = CreateController(db);
        SetupWebhookRequest(ctrl, body, "sha256=badhash");

        var result = await ctrl.Webhook();

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Theory]
    [InlineData("AUTHORIZED",  OrderStatus.Reserved)]
    [InlineData("TERMINATED",  OrderStatus.Failed)]
    [InlineData("REFUNDED",    OrderStatus.Refunded)]
    [InlineData("UNKNOWN_EVT", OrderStatus.Pending)]
    public async Task Webhook_EventNames_MapToCorrectStatus(string eventName, OrderStatus expected)
    {
        using var db = CreateDbContext();
        await db.AppSettings.AddAsync(new AppSettings { Id = 1, VippsShopWebhookSecret = "s" });
        var order = new Order { UserId = "u", TotalInOre = 100, Status = OrderStatus.Pending };
        await db.Orders.AddAsync(order);
        await db.SaveChangesAsync();

        var body = JsonSerializer.Serialize(new { reference = order.Id.ToString(), name = eventName });
        var sig = BuildSig(body, "s");
        var ctrl = CreateController(db);
        SetupWebhookRequest(ctrl, body, sig);
        await ctrl.Webhook();

        var updated = await db.Orders.FindAsync(order.Id);
        Assert.Equal(expected, updated!.Status);
    }

    [Fact]
    public async Task Checkout_InactiveItem_ReturnsBadRequest()
    {
        using var db = CreateDbContext();
        await db.AppSettings.AddAsync(new AppSettings { Id = 1 });
        var item = new ShopItem { Name = "Sensor", PriceInOre = 5000, IsActive = false };
        await db.ShopItems.AddAsync(item);
        await db.SaveChangesAsync();

        var ctrl = CreateController(db);
        var dto = new CreateOrderDto
        {
            Items = [new OrderItemRequestDto { ShopItemId = item.Id, Quantity = 1 }],
            PhoneNumber = "4791234567",
            RedirectUrl = "https://garge.no/shop/return",
            ShippingAddress = "Testgata 1, 0001 Oslo"
        };

        var result = await ctrl.Checkout(dto);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Checkout_InsufficientStock_ReturnsBadRequest()
    {
        using var db = CreateDbContext();
        await db.AppSettings.AddAsync(new AppSettings { Id = 1 });
        var item = new ShopItem { Name = "Sensor", PriceInOre = 5000, IsActive = true, StockCount = 1 };
        await db.ShopItems.AddAsync(item);
        await db.SaveChangesAsync();

        var vipps = MockVipps();
        var ctrl = CreateController(db, vipps: vipps.Object);
        var dto = new CreateOrderDto
        {
            Items = [new OrderItemRequestDto { ShopItemId = item.Id, Quantity = 2 }],
            PhoneNumber = "4791234567",
            RedirectUrl = "https://garge.no/shop/return",
            ShippingAddress = "Testgata 1, 0001 Oslo"
        };

        var result = await ctrl.Checkout(dto);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Checkout_ValidOrder_StoresShippingAddressAndVatSnapshot()
    {
        using var db = CreateDbContext();
        await db.AppSettings.AddAsync(new AppSettings { Id = 1, VatEnabled = true });
        var item = new ShopItem { Name = "Sensor", PriceInOre = 8000, IsActive = true, StockCount = -1 };
        await db.ShopItems.AddAsync(item);
        await db.SaveChangesAsync();

        var vipps = MockVipps();
        vipps.Setup(v => v.CreatePaymentAsync(It.IsAny<Order>(), It.IsAny<List<VippsOrderLine>>(),
                It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new VippsCreatePaymentResponse { Reference = "vipps-ref", RedirectUrl = "https://vipps.no/pay" });

        var ctrl = CreateController(db, vipps: vipps.Object);
        var dto = new CreateOrderDto
        {
            Items = [new OrderItemRequestDto { ShopItemId = item.Id, Quantity = 1 }],
            PhoneNumber = "4791234567",
            RedirectUrl = "https://garge.no/shop/return",
            ShippingAddress = "Testgata 1, 0001 Oslo"
        };

        await ctrl.Checkout(dto);

        var savedOrder = await db.Orders.FirstAsync();
        var savedItem = await db.OrderItems.FirstAsync();

        Assert.Equal("Testgata 1, 0001 Oslo", savedOrder.ShippingAddress);
        Assert.Equal(8000, savedItem.UnitPriceExclVatInOre);
        Assert.Equal(25, savedItem.VatPercentage);
        Assert.Equal(10000, savedItem.PriceAtPurchaseInOre); // 8000 * 1.25
    }

    private static string BuildSig(string body, string secret) =>
        "sha256=" + Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))
        ).ToLowerInvariant();

    private static void SetupWebhookRequest(ShopController ctrl, string body, string signature)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctrl.ControllerContext.HttpContext.Request.Body = new MemoryStream(bytes);
        ctrl.ControllerContext.HttpContext.Request.ContentLength = bytes.Length;
        ctrl.ControllerContext.HttpContext.Request.Headers["X-Vipps-Signature"] = signature;
    }
}
