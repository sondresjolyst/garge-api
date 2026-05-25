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
using Microsoft.Extensions.Options;
using Moq;
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
                It.IsAny<HttpRequest>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((HttpRequest req, string body, string secret) =>
                string.IsNullOrEmpty(secret)
                    ? WebhookVerifyResult.MissingSecret
                    : (req.Headers["X-Test-Valid"] == "1"
                        ? WebhookVerifyResult.Valid
                        : WebhookVerifyResult.BadSignature));
        return mock;
    }

    private static Mock<IWebhookSecretProtector> MockProtector()
    {
        var m = new Mock<IWebhookSecretProtector>();
        m.Setup(p => p.Protect(It.IsAny<string>())).Returns<string>(s => s);
        m.Setup(p => p.Unprotect(It.IsAny<string>())).Returns<string>(s => s);
        return m;
    }

    private static Mock<IAppSettingsCache> MockSettingsCache(AppSettings? settings = null)
    {
        var m = new Mock<IAppSettingsCache>();
        m.Setup(c => c.GetAsync()).ReturnsAsync(settings ?? new AppSettings { Id = 1 });
        return m;
    }

    private static IOptions<VippsOptions> VippsOpts() => Options.Create(new VippsOptions
    {
        ClientId = "id", ClientSecret = "s", MerchantSerialNumber = "msn-prod",
        SubscriptionKey = "k", BaseUrl = "https://api.vipps.no",
        TestMerchantSerialNumber = "msn-test"
    });

    private static IOptions<AppOptions> AppOpts() => Options.Create(new AppOptions
    {
        FrontendBaseUrl = "https://www.garge.no",
        ApiBaseUrl = "https://garge-api.prod.tumogroup.com"
    });

    private ShopController CreateController(
        ApplicationDbContext db, string userId = "user-1",
        Mock<IVippsService>? vipps = null, IInvoiceService? invoice = null,
        IOrderEmailService? orderEmail = null,
        AppSettings? settings = null)
    {
        var push = new Mock<IWebPushService>();
        push.Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var ctrl = new ShopController(
            db, (vipps ?? MockVipps()).Object,
            invoice ?? MockInvoice().Object,
            orderEmail ?? new Mock<IOrderEmailService>().Object,
            MockSettingsCache(settings).Object,
            MockProtector().Object,
            push.Object,
            VippsOpts(),
            AppOpts(),
            MockMapper.Object,
            NullLogger<ShopController>.Instance);
        ctrl.ControllerContext = MakeControllerContext(userId);
        return ctrl;
    }

    [Fact]
    public async Task Webhook_ValidHmac_AuthorizedEvent_SetsReserved()
    {
        using var db = CreateDbContext();
        var order = new Order
        {
            UserId = "user-1", VippsOrderId = "garge-order-000001",
            TotalInOre = 10000, Status = OrderStatus.Pending
        };
        await db.Orders.AddAsync(order, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var payload = new
        {
            reference = order.VippsOrderId,
            pspReference = "psp-1",
            name = "AUTHORIZED",
            amount = new { value = 10000, currency = "NOK" },
            msn = "msn-prod"
        };
        var body = JsonSerializer.Serialize(payload);

        var ctrl = CreateController(db, settings: new AppSettings { Id = 1, VippsShopWebhookSecret = "secret" });
        SetupValidWebhookRequest(ctrl, body);

        var result = await ctrl.Webhook();

        Assert.IsType<OkResult>(result);
        var updated = await db.Orders.FindAsync(new object?[] { order.Id }, TestContext.Current.CancellationToken);
        Assert.Equal(OrderStatus.Reserved, updated!.Status);
    }

    [Fact]
    public async Task Webhook_AuthorizedEvent_SendsConfirmationEmail()
    {
        using var db = CreateDbContext();
        var order = new Order
        {
            UserId = "user-1", VippsOrderId = "garge-order-email-1",
            TotalInOre = 10000, Status = OrderStatus.Pending
        };
        await db.Orders.AddAsync(order, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var orderEmail = new Mock<IOrderEmailService>();
        orderEmail.Setup(o => o.SendOrderConfirmedAsync(order.Id))
            .Returns(Task.CompletedTask).Verifiable();

        var payload = new
        {
            reference = order.VippsOrderId,
            pspReference = "psp-email-1",
            name = "AUTHORIZED",
            amount = new { value = 10000, currency = "NOK" },
            msn = "msn-prod"
        };
        var body = JsonSerializer.Serialize(payload);

        var ctrl = CreateController(db, orderEmail: orderEmail.Object,
            settings: new AppSettings { Id = 1, VippsShopWebhookSecret = "secret" });
        SetupValidWebhookRequest(ctrl, body);

        await ctrl.Webhook();

        orderEmail.Verify(o => o.SendOrderConfirmedAsync(order.Id), Times.Once);
    }

    [Fact]
    public async Task Webhook_CapturedEvent_DoesNotSendConfirmationEmail()
    {
        using var db = CreateDbContext();
        var order = new Order
        {
            UserId = "user-1", VippsOrderId = "garge-order-email-2",
            TotalInOre = 10000, Status = OrderStatus.Reserved
        };
        await db.Orders.AddAsync(order, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var orderEmail = new Mock<IOrderEmailService>();

        var payload = new
        {
            reference = order.VippsOrderId,
            pspReference = "psp-email-2",
            name = "CAPTURED",
            amount = new { value = 10000, currency = "NOK" },
            msn = "msn-prod"
        };
        var body = JsonSerializer.Serialize(payload);

        var ctrl = CreateController(db, orderEmail: orderEmail.Object,
            settings: new AppSettings { Id = 1, VippsShopWebhookSecret = "secret" });
        SetupValidWebhookRequest(ctrl, body);

        await ctrl.Webhook();

        orderEmail.Verify(o => o.SendOrderConfirmedAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Webhook_CapturedEvent_NoExistingInvoice_GeneratesInvoice()
    {
        using var db = CreateDbContext();
        var order = new Order
        {
            UserId = "user-1", VippsOrderId = "garge-order-cap-recover",
            TotalInOre = 10000, Status = OrderStatus.Paid
        };
        await db.Orders.AddAsync(order, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var invoice = new Mock<IInvoiceService>();
        invoice.Setup(i => i.GenerateAndStoreAsync(order.Id, false))
            .ReturnsAsync(42);

        var payload = new
        {
            reference = order.VippsOrderId,
            pspReference = "psp-cap-recover",
            name = "CAPTURED",
            amount = new { value = 10000, currency = "NOK" },
            msn = "msn-prod"
        };
        var body = JsonSerializer.Serialize(payload);

        var ctrl = CreateController(db, invoice: invoice.Object,
            settings: new AppSettings { Id = 1, VippsShopWebhookSecret = "secret" });
        SetupValidWebhookRequest(ctrl, body);

        await ctrl.Webhook();

        invoice.Verify(i => i.GenerateAndStoreAsync(order.Id, false), Times.Once);
    }

    [Fact]
    public async Task Webhook_CapturedEvent_ExistingInvoice_DelegatesToIdempotentService()
    {
        // Webhook now unconditionally calls GenerateAndStoreAsync when an order is
        // already Paid; the service short-circuits when a complete invoice exists.
        // Verify the controller delegates rather than duplicating the check.
        using var db = CreateDbContext();
        var order = new Order
        {
            UserId = "user-1", VippsOrderId = "garge-order-cap-existing",
            TotalInOre = 10000, Status = OrderStatus.Paid
        };
        await db.Orders.AddAsync(order, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.Invoices.Add(new Invoice { OrderId = order.Id, IssuedAt = DateTime.UtcNow, PdfData = [1, 2, 3] });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var invoice = new Mock<IInvoiceService>();
        invoice.Setup(i => i.GenerateAndStoreAsync(order.Id, false)).ReturnsAsync(1);

        var payload = new
        {
            reference = order.VippsOrderId,
            pspReference = "psp-cap-existing",
            name = "CAPTURED",
            amount = new { value = 10000, currency = "NOK" },
            msn = "msn-prod"
        };
        var body = JsonSerializer.Serialize(payload);

        var ctrl = CreateController(db, invoice: invoice.Object,
            settings: new AppSettings { Id = 1, VippsShopWebhookSecret = "secret" });
        SetupValidWebhookRequest(ctrl, body);

        await ctrl.Webhook();

        invoice.Verify(i => i.GenerateAndStoreAsync(order.Id, false), Times.Once);
    }

    [Fact]
    public async Task RegenerateInvoice_AdminCall_PaidOrder_CallsServiceWithForce()
    {
        using var db = CreateDbContext();
        var order = new Order
        {
            UserId = "user-1", VippsOrderId = "garge-order-regen",
            TotalInOre = 10000, Status = OrderStatus.Paid
        };
        await db.Orders.AddAsync(order, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var invoice = new Mock<IInvoiceService>();
        invoice.Setup(i => i.GenerateAndStoreAsync(order.Id, true))
            .ReturnsAsync(7);

        var ctrl = CreateController(db, invoice: invoice.Object,
            settings: new AppSettings { Id = 1, VippsShopWebhookSecret = "secret" });

        var result = await ctrl.RegenerateInvoice(order.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"invoiceId\":7", json);
        invoice.Verify(i => i.GenerateAndStoreAsync(order.Id, true), Times.Once);
    }

    [Fact]
    public async Task RegenerateInvoice_NotPaid_ReturnsBadRequest()
    {
        using var db = CreateDbContext();
        var order = new Order
        {
            UserId = "user-1", VippsOrderId = "garge-order-regen-bad",
            TotalInOre = 10000, Status = OrderStatus.Reserved
        };
        await db.Orders.AddAsync(order, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var invoice = new Mock<IInvoiceService>();
        var ctrl = CreateController(db, invoice: invoice.Object,
            settings: new AppSettings { Id = 1, VippsShopWebhookSecret = "secret" });

        var result = await ctrl.RegenerateInvoice(order.Id);

        Assert.IsType<BadRequestObjectResult>(result);
        invoice.Verify(i => i.GenerateAndStoreAsync(It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task Webhook_AuthorizedEvent_PopulatesShippingFromVippsProfile()
    {
        using var db = CreateDbContext();
        var order = new Order
        {
            UserId = "user-1", VippsOrderId = "garge-order-000002",
            TotalInOre = 10000, Status = OrderStatus.Pending
        };
        await db.Orders.AddAsync(order, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var vipps = MockVipps();
        vipps.Setup(v => v.GetPaymentAsync(order.VippsOrderId))
            .ReturnsAsync(new VippsPaymentResponse
            {
                Reference = order.VippsOrderId, State = "AUTHORIZED", ProfileSub = "sub-abc"
            });
        vipps.Setup(v => v.GetUserInfoAsync("sub-abc"))
            .ReturnsAsync(new VippsUserInfo
            {
                Address = new VippsAddress
                {
                    Formatted = "Mårvegen 21a, 4347 Lye, Norway"
                }
            });

        var payload = new
        {
            reference = order.VippsOrderId,
            pspReference = "psp-2",
            name = "AUTHORIZED",
            amount = new { value = 10000, currency = "NOK" },
            msn = "msn-prod"
        };
        var body = JsonSerializer.Serialize(payload);

        var ctrl = CreateController(db, vipps: vipps,
            settings: new AppSettings { Id = 1, VippsShopWebhookSecret = "secret" });
        SetupValidWebhookRequest(ctrl, body);

        var result = await ctrl.Webhook();

        Assert.IsType<OkResult>(result);
        var updated = await db.Orders.FindAsync(new object?[] { order.Id }, TestContext.Current.CancellationToken);
        Assert.Equal("Mårvegen 21a, 4347 Lye, Norway", updated!.ShippingAddress);
    }

    [Fact]
    public async Task Webhook_AuthorizedEvent_KeepsExistingAddress_WhenVippsHasNone()
    {
        using var db = CreateDbContext();
        var order = new Order
        {
            UserId = "user-1", VippsOrderId = "garge-order-000003",
            TotalInOre = 10000, Status = OrderStatus.Pending,
            ShippingAddress = "Existing address 1"
        };
        await db.Orders.AddAsync(order, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var vipps = MockVipps();
        vipps.Setup(v => v.GetPaymentAsync(order.VippsOrderId))
            .ReturnsAsync(new VippsPaymentResponse { Reference = order.VippsOrderId, State = "AUTHORIZED" });

        var payload = new
        {
            reference = order.VippsOrderId,
            pspReference = "psp-3",
            name = "AUTHORIZED",
            amount = new { value = 10000, currency = "NOK" },
            msn = "msn-prod"
        };
        var body = JsonSerializer.Serialize(payload);

        var ctrl = CreateController(db, vipps: vipps,
            settings: new AppSettings { Id = 1, VippsShopWebhookSecret = "secret" });
        SetupValidWebhookRequest(ctrl, body);

        await ctrl.Webhook();

        var updated = await db.Orders.FindAsync(new object?[] { order.Id }, TestContext.Current.CancellationToken);
        Assert.Equal("Existing address 1", updated!.ShippingAddress);
        vipps.Verify(v => v.GetUserInfoAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Webhook_InvalidHmac_Returns401()
    {
        using var db = CreateDbContext();
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var body = """{"reference":"1","name":"AUTHORIZED"}""";
        var ctrl = CreateController(db, settings: new AppSettings { Id = 1, VippsShopWebhookSecret = "secret" });
        SetupInvalidWebhookRequest(ctrl, body);

        var result = await ctrl.Webhook();

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Theory]
    [InlineData("AUTHORIZED",  OrderStatus.Reserved)]
    [InlineData("CAPTURED",    OrderStatus.Paid)]
    [InlineData("TERMINATED",  OrderStatus.Failed)]
    [InlineData("ABORTED",     OrderStatus.Failed)]
    [InlineData("EXPIRED",     OrderStatus.Failed)]
    [InlineData("CANCELLED",   OrderStatus.Cancelled)]
    [InlineData("REFUNDED",    OrderStatus.Refunded)]
    [InlineData("UNKNOWN_EVT", OrderStatus.Pending)]
    public async Task Webhook_EventNames_MapToCorrectStatus(string eventName, OrderStatus expected)
    {
        using var db = CreateDbContext();
        var order = new Order { UserId = "u", VippsOrderId = $"garge-order-evt-{eventName}", TotalInOre = 100, Status = OrderStatus.Pending };
        await db.Orders.AddAsync(order, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var body = JsonSerializer.Serialize(new
        {
            reference = order.VippsOrderId,
            pspReference = $"psp-{eventName}",
            name = eventName,
            amount = new { value = 100, currency = "NOK" },
            msn = "msn-prod"
        });
        var ctrl = CreateController(db, settings: new AppSettings { Id = 1, VippsShopWebhookSecret = "s" });
        SetupValidWebhookRequest(ctrl, body);
        await ctrl.Webhook();

        var updated = await db.Orders.FindAsync(new object?[] { order.Id }, TestContext.Current.CancellationToken);
        Assert.Equal(expected, updated!.Status);
    }

    [Fact]
    public async Task Webhook_AmountMismatch_Returns401()
    {
        using var db = CreateDbContext();
        var order = new Order { UserId = "u", VippsOrderId = "garge-order-amount", TotalInOre = 10000, Status = OrderStatus.Pending };
        await db.Orders.AddAsync(order, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var body = JsonSerializer.Serialize(new
        {
            reference = order.VippsOrderId,
            pspReference = "psp-bad",
            name = "AUTHORIZED",
            amount = new { value = 9999, currency = "NOK" },
            msn = "msn-prod"
        });
        var ctrl = CreateController(db, settings: new AppSettings { Id = 1, VippsShopWebhookSecret = "s" });
        SetupValidWebhookRequest(ctrl, body);

        var result = await ctrl.Webhook();
        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task Webhook_DuplicateEvent_Idempotent()
    {
        using var db = CreateDbContext();
        var order = new Order { UserId = "u", VippsOrderId = "garge-order-dup", TotalInOre = 100, Status = OrderStatus.Pending };
        await db.Orders.AddAsync(order, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var body = JsonSerializer.Serialize(new
        {
            reference = order.VippsOrderId,
            pspReference = "psp-once",
            name = "AUTHORIZED",
            amount = new { value = 100, currency = "NOK" },
            msn = "msn-prod"
        });

        var settings = new AppSettings { Id = 1, VippsShopWebhookSecret = "s" };

        var c1 = CreateController(db, settings: settings);
        SetupValidWebhookRequest(c1, body);
        await c1.Webhook();

        var c2 = CreateController(db, settings: settings);
        SetupValidWebhookRequest(c2, body);
        await c2.Webhook();

        Assert.Equal(1, await db.ProcessedWebhookEvents.CountAsync(e => e.Id == "psp-once", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Checkout_InactiveItem_ReturnsBadRequest()
    {
        using var db = CreateDbContext();
        var item = new ShopItem { Name = "Sensor", PriceInOre = 5000, IsActive = false };
        await db.ShopItems.AddAsync(item, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var ctrl = CreateController(db);
        var dto = new CreateOrderDto
        {
            Items = [new OrderItemRequestDto { ShopItemId = item.Id, Quantity = 1 }],
            PhoneNumber = "4791234567",
            ShippingAddress = "Testgata 1, 0001 Oslo"
        };

        var result = await ctrl.Checkout(dto);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Checkout_InsufficientStock_ReturnsBadRequest()
    {
        using var db = CreateDbContext();
        var item = new ShopItem { Name = "Sensor", PriceInOre = 5000, IsActive = true, StockCount = 1 };
        await db.ShopItems.AddAsync(item, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var ctrl = CreateController(db);
        var dto = new CreateOrderDto
        {
            Items = [new OrderItemRequestDto { ShopItemId = item.Id, Quantity = 2 }],
            PhoneNumber = "4791234567",
            ShippingAddress = "Testgata 1, 0001 Oslo"
        };

        var result = await ctrl.Checkout(dto);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Checkout_InvalidPhone_ReturnsBadRequest()
    {
        using var db = CreateDbContext();
        var item = new ShopItem { Name = "Sensor", PriceInOre = 5000, IsActive = true };
        await db.ShopItems.AddAsync(item, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var ctrl = CreateController(db);
        var dto = new CreateOrderDto
        {
            Items = [new OrderItemRequestDto { ShopItemId = item.Id, Quantity = 1 }],
            PhoneNumber = "1234",
            ShippingAddress = "Testgata 1, 0001 Oslo"
        };

        var result = await ctrl.Checkout(dto);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Checkout_ValidOrder_StoresShippingAddressAndVatSnapshot()
    {
        using var db = CreateDbContext();
        var item = new ShopItem { Name = "Sensor", PriceInOre = 8000, IsActive = true, StockCount = -1 };
        await db.ShopItems.AddAsync(item, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var vipps = MockVipps();
        vipps.Setup(v => v.CreatePaymentAsync(It.IsAny<Order>(), It.IsAny<List<VippsOrderLine>>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new VippsCreatePaymentResponse { Reference = "vipps-ref", RedirectUrl = "https://vipps.no/pay" });

        var ctrl = CreateController(db, vipps: vipps,
            settings: new AppSettings { Id = 1, VatEnabled = true });
        var dto = new CreateOrderDto
        {
            Items = [new OrderItemRequestDto { ShopItemId = item.Id, Quantity = 1 }],
            PhoneNumber = "4791234567",
            ShippingAddress = "Testgata 1, 0001 Oslo"
        };

        await ctrl.Checkout(dto);

        var savedOrder = await db.Orders.FirstAsync(TestContext.Current.CancellationToken);
        var savedItem = await db.OrderItems.FirstAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Testgata 1, 0001 Oslo", savedOrder.ShippingAddress);
        Assert.Equal(8000, savedItem.UnitPriceExclVatInOre);
        Assert.Equal(25, savedItem.VatPercentage);
        Assert.Equal(10000, savedItem.PriceAtPurchaseInOre);
    }

    [Fact]
    public async Task Checkout_DecrementsStock()
    {
        using var db = CreateDbContext();
        var item = new ShopItem { Name = "Sensor", PriceInOre = 5000, IsActive = true, StockCount = 3 };
        await db.ShopItems.AddAsync(item, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var vipps = MockVipps();
        vipps.Setup(v => v.CreatePaymentAsync(It.IsAny<Order>(), It.IsAny<List<VippsOrderLine>>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new VippsCreatePaymentResponse { Reference = "ref", RedirectUrl = "x" });

        var ctrl = CreateController(db, vipps: vipps);
        await ctrl.Checkout(new CreateOrderDto
        {
            Items = [new OrderItemRequestDto { ShopItemId = item.Id, Quantity = 2 }],
            PhoneNumber = "4791234567",
            ShippingAddress = "Test"
        });

        var reloaded = await db.ShopItems.FirstAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, reloaded.StockCount);
    }

    [Fact]
    public async Task RefundOrder_PaidOrder_CallsVippsAndSetsStatusToRefunded()
    {
        using var db = CreateDbContext();
        var order = new Order
        {
            UserId = "user-1", VippsOrderId = "garge-order-000007",
            TotalInOre = 12500, Status = OrderStatus.Paid
        };
        await db.Orders.AddAsync(order, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var vipps = MockVipps();
        vipps.Setup(v => v.RefundPaymentAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        var ctrl = CreateController(db, vipps: vipps);
        var result = await ctrl.RefundOrder(order.Id);

        Assert.IsType<OkResult>(result);
        vipps.Verify(v => v.RefundPaymentAsync("garge-order-000007", 12500, $"refund-{order.Id}"), Times.Once);
        var updated = await db.Orders.FindAsync(new object?[] { order.Id }, TestContext.Current.CancellationToken);
        Assert.Equal(OrderStatus.Refunded, updated!.Status);
    }

    [Fact]
    public async Task RefundOrder_NonPaidOrder_Returns400AndDoesNotCallVipps()
    {
        using var db = CreateDbContext();
        var order = new Order
        {
            UserId = "user-1", VippsOrderId = "garge-order-000008",
            TotalInOre = 5000, Status = OrderStatus.Reserved
        };
        await db.Orders.AddAsync(order, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var vipps = MockVipps();
        var ctrl = CreateController(db, vipps: vipps);
        var result = await ctrl.RefundOrder(order.Id);

        Assert.IsType<BadRequestObjectResult>(result);
        vipps.Verify(v => v.RefundPaymentAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);
        var updated = await db.Orders.FindAsync(new object?[] { order.Id }, TestContext.Current.CancellationToken);
        Assert.Equal(OrderStatus.Reserved, updated!.Status);
    }

    [Fact]
    public async Task RefundOrder_PaidOrderMissingVippsOrderId_Returns400()
    {
        using var db = CreateDbContext();
        var order = new Order
        {
            UserId = "user-1", VippsOrderId = null,
            TotalInOre = 5000, Status = OrderStatus.Paid
        };
        await db.Orders.AddAsync(order, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var vipps = MockVipps();
        var ctrl = CreateController(db, vipps: vipps);
        var result = await ctrl.RefundOrder(order.Id);

        Assert.IsType<BadRequestObjectResult>(result);
        vipps.Verify(v => v.RefundPaymentAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RefundOrder_UnknownOrder_Returns404()
    {
        using var db = CreateDbContext();
        var ctrl = CreateController(db);
        var result = await ctrl.RefundOrder(9999);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task RefundOrder_VippsThrows_StatusStaysPaid()
    {
        using var db = CreateDbContext();
        var order = new Order
        {
            UserId = "user-1", VippsOrderId = "garge-order-000009",
            TotalInOre = 7700, Status = OrderStatus.Paid
        };
        await db.Orders.AddAsync(order, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var vipps = MockVipps();
        vipps.Setup(v => v.RefundPaymentAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>()))
            .ThrowsAsync(new HttpRequestException("Vipps unavailable"));

        var ctrl = CreateController(db, vipps: vipps);

        await Assert.ThrowsAsync<HttpRequestException>(() => ctrl.RefundOrder(order.Id));

        var updated = await db.Orders.FindAsync(new object?[] { order.Id }, TestContext.Current.CancellationToken);
        Assert.Equal(OrderStatus.Paid, updated!.Status);
    }

    private static void SetupValidWebhookRequest(ShopController ctrl, string body) =>
        SetupWebhookRequest(ctrl, body, valid: true);

    private static void SetupInvalidWebhookRequest(ShopController ctrl, string body) =>
        SetupWebhookRequest(ctrl, body, valid: false);

    private static void SetupWebhookRequest(ShopController ctrl, string body, bool valid)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctrl.ControllerContext.HttpContext.Request.Body = new MemoryStream(bytes);
        ctrl.ControllerContext.HttpContext.Request.ContentLength = bytes.Length;
        if (valid)
            ctrl.ControllerContext.HttpContext.Request.Headers["X-Test-Valid"] = "1";
    }
}
