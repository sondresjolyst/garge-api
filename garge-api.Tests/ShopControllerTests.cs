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
                       && (string.IsNullOrEmpty(req.Headers["X-Test-Secret"]) || req.Headers["X-Test-Secret"] == secret)
                        ? WebhookVerifyResult.Valid
                        : WebhookVerifyResult.BadSignature));
        // Vipps reports the whole amount done unless a test says otherwise.
        mock.Setup(v => v.CapturePaymentAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool?>()))
            .ReturnsAsync((string _, int amount, string _, bool? _) => new VippsPaymentResponse { CapturedAmountInOre = amount });
        mock.Setup(v => v.RefundPaymentAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool?>()))
            .ReturnsAsync((string _, int amount, string _, bool? _) => new VippsPaymentResponse { CapturedAmountInOre = amount, RefundedAmountInOre = amount });
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
        push.Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
            success = true,
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
            success = true,
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
            success = true,
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
            success = true,
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
            success = true,
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
        vipps.Setup(v => v.GetPaymentAsync(order.VippsOrderId, order.IsTest))
            .ReturnsAsync(new VippsPaymentResponse
            {
                Reference = order.VippsOrderId, State = "AUTHORIZED", ProfileSub = "sub-abc"
            });
        vipps.Setup(v => v.GetUserInfoAsync("sub-abc", false))
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
            success = true,
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
        vipps.Setup(v => v.GetPaymentAsync(order.VippsOrderId, order.IsTest))
            .ReturnsAsync(new VippsPaymentResponse { Reference = order.VippsOrderId, State = "AUTHORIZED" });

        var payload = new
        {
            reference = order.VippsOrderId,
            pspReference = "psp-3",
            name = "AUTHORIZED",
            success = true,
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
        vipps.Verify(v => v.GetUserInfoAsync(It.IsAny<string>(), It.IsAny<bool?>()), Times.Never);
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
            success = true,
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
    public async Task Webhook_AmountMismatch_IsAcknowledgedButNotApplied()
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
            success = true,
            amount = new { value = 9999, currency = "NOK" },
            msn = "msn-prod"
        });
        var ctrl = CreateController(db, settings: new AppSettings { Id = 1, VippsShopWebhookSecret = "s" });
        SetupValidWebhookRequest(ctrl, body);

        var result = await ctrl.Webhook();
        Assert.IsType<OkResult>(result);
        Assert.Equal(OrderStatus.Pending, (await db.Orders.AsNoTracking().FirstAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Equal(1, await db.ProcessedWebhookEvents.CountAsync(TestContext.Current.CancellationToken));
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
            success = true,
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
        var item = new ShopItem { Name = "Sensor", PriceInOre = 10000, IsActive = true, StockCount = -1 };
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AdminPaymentActions_UseTheEnvironmentTheOrderWasPaidIn(bool orderIsTest)
    {
        using var db = CreateDbContext();
        var reserved = new Order { UserId = "user-1", VippsOrderId = "garge-order-cap", TotalInOre = 10000, Status = OrderStatus.Reserved, IsTest = orderIsTest };
        var toCancel = new Order { UserId = "user-1", VippsOrderId = "garge-order-can", TotalInOre = 10000, Status = OrderStatus.Reserved, IsTest = orderIsTest };
        var paid = new Order { UserId = "user-1", VippsOrderId = "garge-order-ref", TotalInOre = 10000, Status = OrderStatus.Paid, IsTest = orderIsTest };
        await db.Orders.AddRangeAsync([reserved, toCancel, paid], TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var vipps = MockVipps();
        var ctrl = CreateController(db, vipps: vipps);

        Assert.IsType<OkResult>(await ctrl.CaptureOrder(reserved.Id));
        Assert.IsType<OkResult>(await ctrl.CancelOrder(toCancel.Id));
        Assert.IsType<OkResult>(await ctrl.RefundOrder(paid.Id));

        vipps.Verify(v => v.CapturePaymentAsync("garge-order-cap", 10000, $"capture-{reserved.Id}", orderIsTest), Times.Once);
        vipps.Verify(v => v.CancelPaymentAsync("garge-order-can", $"cancel-{toCancel.Id}", orderIsTest), Times.Once);
        vipps.Verify(v => v.RefundPaymentAsync("garge-order-ref", 10000, $"refund-{paid.Id}", orderIsTest), Times.Once);
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
        var ctrl = CreateController(db, vipps: vipps);
        var result = await ctrl.RefundOrder(order.Id);

        Assert.IsType<OkResult>(result);
        vipps.Verify(v => v.RefundPaymentAsync("garge-order-000007", 12500, $"refund-{order.Id}", false), Times.Once);
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
        vipps.Verify(v => v.RefundPaymentAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool?>()), Times.Never);
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
        vipps.Verify(v => v.RefundPaymentAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool?>()), Times.Never);
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
        vipps.Setup(v => v.RefundPaymentAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool?>()))
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

    private static string ShopEvent(string reference, string? msn, int amount = 10000, string name = "AUTHORIZED", string psp = "psp-money", bool success = true) =>
        JsonSerializer.Serialize(new { reference, pspReference = psp, name, amount = new { value = amount, currency = "NOK" }, msn, success });

    [Theory]
    [InlineData(true, "test-secret", "msn-test", OrderStatus.Reserved)]
    [InlineData(false, "live-secret", "msn-prod", OrderStatus.Reserved)]
    [InlineData(true, "live-secret", "msn-test", OrderStatus.Pending)]
    [InlineData(false, "test-secret", "msn-prod", OrderStatus.Pending)]
    public async Task Webhook_IsOnlyAppliedFromTheOrdersOwnEnvironment(bool orderIsTest, string signedWith, string msn, OrderStatus expected)
    {
        using var db = CreateDbContext();
        var order = new Order { UserId = "user-1", VippsOrderId = "garge-order-env", TotalInOre = 10000, Status = OrderStatus.Pending, IsTest = orderIsTest };
        await db.Orders.AddAsync(order, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var ctrl = CreateController(db, settings: new AppSettings
        {
            Id = 1,
            VippsShopWebhookId = "wh-live", VippsShopWebhookSecret = "live-secret",
            VippsTestShopWebhookId = "wh-test", VippsTestShopWebhookSecret = "test-secret"
        });
        SetupValidWebhookRequest(ctrl, ShopEvent(order.VippsOrderId, msn));
        ctrl.ControllerContext.HttpContext.Request.Headers["X-Test-Secret"] = signedWith;

        Assert.IsType<OkResult>(await ctrl.Webhook());
        Assert.Equal(expected, (await db.Orders.AsNoTracking().FirstAsync(TestContext.Current.CancellationToken)).Status);
        var key = await db.ProcessedWebhookEvents.Select(e => e.Id).SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(signedWith == "test-secret" ? "test:psp-money" : "psp-money", key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("msn-other")]
    [InlineData("msn-test")]
    public async Task Webhook_MerchantNumberNotMatching_IsAcknowledgedButNotApplied(string? msn)
    {
        using var db = CreateDbContext();
        var order = new Order { UserId = "user-1", VippsOrderId = "garge-order-msn", TotalInOre = 10000, Status = OrderStatus.Pending };
        await db.Orders.AddAsync(order, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var ctrl = CreateController(db, settings: new AppSettings { Id = 1, VippsShopWebhookSecret = "secret" });
        SetupValidWebhookRequest(ctrl, ShopEvent(order.VippsOrderId!, msn));

        // 200, so Vipps does not retry it for a week and hold back the order's later events.
        Assert.IsType<OkResult>(await ctrl.Webhook());
        Assert.Equal(1, await db.ProcessedWebhookEvents.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(OrderStatus.Pending, (await db.Orders.AsNoTracking().FirstAsync(TestContext.Current.CancellationToken)).Status);
    }

    [Theory]
    [InlineData("AUTHORIZED")]
    [InlineData("CAPTURED")]
    [InlineData("REFUNDED")]
    [InlineData("CANCELLED")]
    public async Task Webhook_OperationThatDidNotSucceed_LeavesTheOrderUnchanged(string name)
    {
        using var db = CreateDbContext();
        var order = new Order { UserId = "user-1", VippsOrderId = "garge-order-fail", TotalInOre = 10000, Status = OrderStatus.Reserved };
        await db.Orders.AddAsync(order, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var invoice = new Mock<IInvoiceService>();
        var ctrl = CreateController(db, invoice: invoice.Object, settings: new AppSettings { Id = 1, VippsShopWebhookSecret = "secret" });
        SetupValidWebhookRequest(ctrl, ShopEvent(order.VippsOrderId!, "msn-prod", name: name, success: false));

        Assert.IsType<OkResult>(await ctrl.Webhook());
        Assert.Equal(OrderStatus.Reserved, (await db.Orders.AsNoTracking().FirstAsync(TestContext.Current.CancellationToken)).Status);
        invoice.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Webhook_AuthorizedWithoutAmount_IsAcknowledgedButNotApplied()
    {
        using var db = CreateDbContext();
        var order = new Order { UserId = "user-1", VippsOrderId = "garge-order-noamt", TotalInOre = 10000, Status = OrderStatus.Pending };
        await db.Orders.AddAsync(order, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var body = JsonSerializer.Serialize(new { reference = order.VippsOrderId, pspReference = "psp-noamt", name = "AUTHORIZED", msn = "msn-prod", success = true });
        var ctrl = CreateController(db, settings: new AppSettings { Id = 1, VippsShopWebhookSecret = "secret" });
        SetupValidWebhookRequest(ctrl, body);

        Assert.IsType<OkResult>(await ctrl.Webhook());
        Assert.Equal(OrderStatus.Pending, (await db.Orders.AsNoTracking().FirstAsync(TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Webhook_FailedSave_IsNotMarkedProcessed_AndTheRedeliveryIsApplied()
    {
        var (failing, healthy) = FailingSaveInterceptor.Contexts();
        using (failing)
        using (healthy)
        {
            var order = new Order { UserId = "user-1", VippsOrderId = "garge-order-retry", TotalInOre = 10000, Status = OrderStatus.Pending };
            await healthy.Orders.AddAsync(order, TestContext.Current.CancellationToken);
            await healthy.SaveChangesAsync(TestContext.Current.CancellationToken);
            var settings = new AppSettings { Id = 1, VippsShopWebhookSecret = "secret" };
            var body = ShopEvent(order.VippsOrderId!, "msn-prod");

            var first = CreateController(failing, settings: settings);
            SetupValidWebhookRequest(first, body);
            await Assert.ThrowsAsync<DbUpdateException>(() => first.Webhook());
            Assert.Equal(0, await healthy.ProcessedWebhookEvents.CountAsync(TestContext.Current.CancellationToken));

            var redelivery = CreateController(healthy, settings: settings);
            SetupValidWebhookRequest(redelivery, body);
            Assert.IsType<OkResult>(await redelivery.Webhook());

            Assert.Equal(1, await healthy.ProcessedWebhookEvents.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(OrderStatus.Reserved, (await healthy.Orders.AsNoTracking().FirstAsync(TestContext.Current.CancellationToken)).Status);
        }
    }

    private static Mock<IVippsService> VippsReporting(int captured = 0, int refunded = 0)
    {
        var vipps = MockVipps();
        vipps.Setup(v => v.GetPaymentAsync(It.IsAny<string>(), It.IsAny<bool?>()))
            .ReturnsAsync(new VippsPaymentResponse { CapturedAmountInOre = captured, RefundedAmountInOre = refunded });
        return vipps;
    }

    private async Task<Order> SeedOrderAsync(ApplicationDbContext db, string reference, OrderStatus status, int total = 10000, int stockLeft = 3, int quantity = 2)
    {
        var item = new ShopItem { Name = "Sensor", PriceInOre = 5000, IsActive = true, StockCount = stockLeft };
        await db.ShopItems.AddAsync(item, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var order = new Order { UserId = "user-1", VippsOrderId = reference, TotalInOre = total, Status = status };
        order.OrderItems.Add(new OrderItem { ShopItemId = item.Id, Quantity = quantity, PriceAtPurchaseInOre = 5000 });
        await db.Orders.AddAsync(order, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return order;
    }

    private async Task<IActionResult> DeliverAsync(ApplicationDbContext db, string body, Mock<IVippsService>? vipps = null, IInvoiceService? invoice = null)
    {
        var ctrl = CreateController(db, vipps: vipps, invoice: invoice, settings: new AppSettings { Id = 1, VippsShopWebhookSecret = "secret" });
        SetupValidWebhookRequest(ctrl, body);
        return await ctrl.Webhook();
    }

    [Fact]
    public async Task Webhook_PartialCapture_KeepsTheOrderReserved_ThenTheRestMakesItPaid()
    {
        using var db = CreateDbContext();
        var order = await SeedOrderAsync(db, "garge-order-partial", OrderStatus.Reserved);
        var invoice = new Mock<IInvoiceService>();

        await DeliverAsync(db, ShopEvent(order.VippsOrderId!, "msn-prod", amount: 4000, name: "CAPTURED", psp: "psp-cap-1"), VippsReporting(captured: 4000), invoice.Object);
        Assert.Equal(OrderStatus.Reserved, (await db.Orders.AsNoTracking().FirstAsync(TestContext.Current.CancellationToken)).Status);
        invoice.VerifyNoOtherCalls();

        await DeliverAsync(db, ShopEvent(order.VippsOrderId!, "msn-prod", amount: 6000, name: "CAPTURED", psp: "psp-cap-2"), VippsReporting(captured: 10000), invoice.Object);
        Assert.Equal(OrderStatus.Paid, (await db.Orders.AsNoTracking().FirstAsync(TestContext.Current.CancellationToken)).Status);
    }

    [Theory]
    [InlineData(10001)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Webhook_CaptureOutsideTheOrderTotal_IsAcknowledgedButNotApplied(int amount)
    {
        using var db = CreateDbContext();
        var order = await SeedOrderAsync(db, "garge-order-over", OrderStatus.Reserved);

        Assert.IsType<OkResult>(await DeliverAsync(db, ShopEvent(order.VippsOrderId!, "msn-prod", amount: amount, name: "CAPTURED"), VippsReporting(captured: 10000)));
        Assert.Equal(OrderStatus.Reserved, (await db.Orders.AsNoTracking().FirstAsync(TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Webhook_PartialRefund_KeepsTheOrderPaid_AndAFullRefundRefundsIt()
    {
        using var db = CreateDbContext();
        var order = await SeedOrderAsync(db, "garge-order-refund", OrderStatus.Paid);

        await DeliverAsync(db, ShopEvent(order.VippsOrderId!, "msn-prod", amount: 2500, name: "REFUNDED", psp: "psp-ref-1"), VippsReporting(captured: 10000, refunded: 2500));
        Assert.Equal(OrderStatus.Paid, (await db.Orders.AsNoTracking().FirstAsync(TestContext.Current.CancellationToken)).Status);

        await DeliverAsync(db, ShopEvent(order.VippsOrderId!, "msn-prod", amount: 7500, name: "REFUNDED", psp: "psp-ref-2"), VippsReporting(captured: 10000, refunded: 10000));
        Assert.Equal(OrderStatus.Refunded, (await db.Orders.AsNoTracking().FirstAsync(TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Webhook_CancelAfterAPartialCapture_KeepsTheOrderAndItsStock()
    {
        using var db = CreateDbContext();
        var order = await SeedOrderAsync(db, "garge-order-cancel-part", OrderStatus.Reserved, stockLeft: 3, quantity: 2);

        await DeliverAsync(db, ShopEvent(order.VippsOrderId!, "msn-prod", amount: 6000, name: "CANCELLED"), VippsReporting(captured: 4000));

        Assert.Equal(OrderStatus.Reserved, (await db.Orders.AsNoTracking().FirstAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Equal(3, (await db.ShopItems.AsNoTracking().FirstAsync(TestContext.Current.CancellationToken)).StockCount);
    }

    [Fact]
    public async Task Webhook_CancelWithNothingCaptured_CancelsAndGivesTheStockBack()
    {
        using var db = CreateDbContext();
        var order = await SeedOrderAsync(db, "garge-order-cancel", OrderStatus.Reserved, stockLeft: 3, quantity: 2);

        await DeliverAsync(db, ShopEvent(order.VippsOrderId!, "msn-prod", name: "CANCELLED"), VippsReporting(captured: 0));

        Assert.Equal(OrderStatus.Cancelled, (await db.Orders.AsNoTracking().FirstAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Equal(5, (await db.ShopItems.AsNoTracking().FirstAsync(TestContext.Current.CancellationToken)).StockCount);
    }

    [Theory]
    [InlineData("ABORTED")]
    [InlineData("EXPIRED")]
    [InlineData("TERMINATED")]
    public async Task Webhook_AbandonedPayment_GivesTheStockBack(string name)
    {
        using var db = CreateDbContext();
        var order = await SeedOrderAsync(db, $"garge-order-{name.ToLowerInvariant()}", OrderStatus.Pending, stockLeft: 3, quantity: 2);

        await DeliverAsync(db, ShopEvent(order.VippsOrderId!, "msn-prod", name: name));

        Assert.Equal(OrderStatus.Failed, (await db.Orders.AsNoTracking().FirstAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Equal(5, (await db.ShopItems.AsNoTracking().FirstAsync(TestContext.Current.CancellationToken)).StockCount);
    }

    [Fact]
    public async Task Webhook_OperationThatDidNotSucceed_DoesNotConfirmAPendingOrder()
    {
        using var db = CreateDbContext();
        var order = await SeedOrderAsync(db, "garge-order-nosuccess", OrderStatus.Pending);
        var orderEmail = new Mock<IOrderEmailService>();
        var ctrl = CreateController(db, orderEmail: orderEmail.Object, settings: new AppSettings { Id = 1, VippsShopWebhookSecret = "secret" });
        SetupValidWebhookRequest(ctrl, ShopEvent(order.VippsOrderId!, "msn-prod", success: false));

        Assert.IsType<OkResult>(await ctrl.Webhook());
        Assert.Equal(OrderStatus.Pending, (await db.Orders.AsNoTracking().FirstAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Equal(1, await db.ProcessedWebhookEvents.CountAsync(TestContext.Current.CancellationToken));
        orderEmail.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Webhook_LosingAConcurrentDelivery_SavesNothing()
    {
        // Two deliveries of one event pass the duplicate check at the same time. The other one saves
        // first, during this one's save, so this one must store neither its order change nor its stock.
        var name = Guid.NewGuid().ToString();
        DbContextOptions<ApplicationDbContext> Options(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] i) =>
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(name)
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
                .AddInterceptors(i).Options;
        using var other = new ApplicationDbContext(Options());
        var order = await SeedOrderAsync(other, "garge-order-race", OrderStatus.Reserved, stockLeft: 3, quantity: 2);
        using var loser = new ApplicationDbContext(Options(new OtherDeliveryWinsInterceptor(Options(), "psp-race")));

        var result = await DeliverAsync(loser, ShopEvent(order.VippsOrderId!, "msn-prod", name: "CANCELLED", psp: "psp-race"), VippsReporting(captured: 0));

        Assert.IsType<OkResult>(result);
        using var check = new ApplicationDbContext(Options());
        Assert.Equal(OrderStatus.Reserved, (await check.Orders.AsNoTracking().FirstAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Equal(3, (await check.ShopItems.AsNoTracking().FirstAsync(TestContext.Current.CancellationToken)).StockCount);
        Assert.Equal(1, await check.ProcessedWebhookEvents.CountAsync(TestContext.Current.CancellationToken));
    }

    private sealed class OtherDeliveryWinsInterceptor(DbContextOptions<ApplicationDbContext> options, string eventId)
        : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        private bool _done;

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_done && eventData.Context!.ChangeTracker.Entries<garge_api.Models.Webhook.ProcessedWebhookEvent>().Any())
            {
                _done = true;
                using var winner = new ApplicationDbContext(options);
                winner.ProcessedWebhookEvents.Add(new garge_api.Models.Webhook.ProcessedWebhookEvent { Id = eventId, Source = "shop" });
                await winner.SaveChangesAsync(cancellationToken);
            }
            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Checkout_TakesTheEnvironmentFromThePayment(bool createdInTest)
    {
        using var db = CreateDbContext();
        var item = new ShopItem { Name = "Sensor", PriceInOre = 5000, IsActive = true, StockCount = -1 };
        await db.ShopItems.AddAsync(item, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var vipps = MockVipps();
        vipps.Setup(v => v.CreatePaymentAsync(It.IsAny<Order>(), It.IsAny<List<VippsOrderLine>>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new VippsCreatePaymentResponse { Reference = "ref-env", RedirectUrl = "x", IsTest = createdInTest });

        // The settings say the opposite, as right after an admin switches test mode.
        var ctrl = CreateController(db, vipps: vipps, settings: new AppSettings { Id = 1, VippsTestMode = !createdInTest });
        await ctrl.Checkout(new CreateOrderDto
        {
            Items = [new OrderItemRequestDto { ShopItemId = item.Id, Quantity = 1 }],
            PhoneNumber = "4791234567"
        });

        Assert.Equal(createdInTest, (await db.Orders.AsNoTracking().FirstAsync(TestContext.Current.CancellationToken)).IsTest);
    }

    [Fact]
    public async Task CaptureOrder_VippsReportsLessCaptured_OrderStaysReserved()
    {
        using var db = CreateDbContext();
        var order = new Order { UserId = "user-1", VippsOrderId = "garge-order-part", TotalInOre = 10000, Status = OrderStatus.Reserved };
        await db.Orders.AddAsync(order, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var vipps = MockVipps();
        vipps.Setup(v => v.CapturePaymentAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool?>()))
            .ReturnsAsync(new VippsPaymentResponse { CapturedAmountInOre = 9999 });

        var result = await CreateController(db, vipps: vipps).CaptureOrder(order.Id);

        Assert.Equal(502, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(OrderStatus.Reserved, (await db.Orders.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Equal(0, await db.Invoices.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RefundOrder_VippsReportsLessRefunded_OrderStaysPaid()
    {
        using var db = CreateDbContext();
        var order = new Order { UserId = "user-1", VippsOrderId = "garge-order-partref", TotalInOre = 10000, Status = OrderStatus.Paid };
        await db.Orders.AddAsync(order, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var vipps = MockVipps();
        vipps.Setup(v => v.RefundPaymentAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool?>()))
            .ReturnsAsync(new VippsPaymentResponse { CapturedAmountInOre = 10000, RefundedAmountInOre = 5000 });

        var result = await CreateController(db, vipps: vipps).RefundOrder(order.Id);

        Assert.Equal(502, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(OrderStatus.Paid, (await db.Orders.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).Status);
    }

    [Theory]
    [InlineData(false, 20000, 0)]
    [InlineData(true, 16000, 25)]
    public async Task Checkout_CustomersPayTheSamePriceWithVatOnOrOff(bool vatEnabled, int exclVat, int vatPercent)
    {
        using var db = CreateDbContext();
        var item = new ShopItem { Name = "Sensor", PriceInOre = 20000, IsActive = true, StockCount = -1 };
        await db.ShopItems.AddAsync(item, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var vipps = MockVipps();
        List<VippsOrderLine>? sentLines = null;
        vipps.Setup(v => v.CreatePaymentAsync(It.IsAny<Order>(), It.IsAny<List<VippsOrderLine>>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback((Order _, List<VippsOrderLine> lines, string _, string _, string _) => sentLines = lines)
            .ReturnsAsync(new VippsCreatePaymentResponse { Reference = "ref", RedirectUrl = "https://vipps.no/pay" });

        var ctrl = CreateController(db, vipps: vipps, settings: new AppSettings { Id = 1, VatEnabled = vatEnabled });
        await ctrl.Checkout(new CreateOrderDto { Items = [new OrderItemRequestDto { ShopItemId = item.Id, Quantity = 2 }], PhoneNumber = "4791234567" });

        Assert.Equal(40000, (await db.Orders.SingleAsync(TestContext.Current.CancellationToken)).TotalInOre);
        var saved = await db.OrderItems.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(20000, saved.PriceAtPurchaseInOre);
        Assert.Equal(exclVat, saved.UnitPriceExclVatInOre);
        Assert.Equal(vatPercent, saved.VatPercentage);
        var line = Assert.Single(sentLines!);
        Assert.Equal(20000, line.UnitPriceInOre);
        Assert.Equal(exclVat, line.UnitPriceExclVatInOre);
        Assert.Equal(vatEnabled ? Pricing.VatBasisPoints : 0, line.TaxPercentageBasisPoints);
    }
}
