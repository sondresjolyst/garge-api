using garge_api.Controllers;
using garge_api.Dtos.Subscription;
using garge_api.Models;
using garge_api.Models.Subscription;
using garge_api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace garge_api.Tests;

/// <summary>A product's price changes only with notice while anyone subscribes to it.</summary>
public class ProductsControllerPriceTests : ControllerTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ProductsController Create(ApplicationDbContext db)
    {
        var priceChanges = new PriceChangeService(db, new Mock<IVippsService>().Object, new Mock<ISubscriptionEmailService>().Object,
            Options.Create(new VippsSettings { BaseUrl = "https://api.vipps.no", ClientId = "id", ClientSecret = "s", SubscriptionKey = "k", MerchantSerialNumber = "m" }), TimeProvider.System, NullLogger<PriceChangeService>.Instance);
        var sweep = new PriceChangeSweepService(new Mock<IServiceScopeFactory>().Object, NullLogger<PriceChangeSweepService>.Instance);
        return new ProductsController(db, priceChanges, sweep, RealMapper, NullLogger<ProductsController>.Instance)
        {
            ControllerContext = MakeControllerContext("admin-1", isAdmin: true),
        };
    }

    private async Task<ApplicationDbContext> SeedAsync(SubscriptionStatus? status = SubscriptionStatus.Active)
    {
        var db = CreateDbContext();
        db.Products.Add(new Product { Id = 1, Name = "Basic", PriceInOre = 4900, Interval = BillingInterval.Monthly, Type = ProductType.Primary, IsActive = true });
        if (status != null)
            db.Subscriptions.Add(new Subscription { UserId = "u", ProductId = 1, UnitPriceInOre = 4900, VippsAgreementId = "a", Status = status.Value });
        await db.SaveChangesAsync(Ct);
        return db;
    }

    private static UpdateProductDto Edit(int price, string name = "Basic") =>
        new() { Name = name, PriceInOre = price, Interval = BillingInterval.Monthly, Type = ProductType.Primary, IsActive = true };

    [Fact]
    public async Task Update_AnIncreaseWithSubscribers_IsAConflict_AndChangesNothing()
    {
        using var db = await SeedAsync();

        var result = await Create(db).UpdateProduct(1, Edit(5900, "Renamed"));

        Assert.IsType<ConflictObjectResult>(result);
        var product = await db.Products.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(4900, product.PriceInOre);
        Assert.Equal("Basic", product.Name);
    }

    [Fact]
    public async Task Update_AnIncreaseWithoutSubscribers_AppliesAtOnce()
    {
        using var db = await SeedAsync(status: null);

        Assert.IsType<OkObjectResult>(await Create(db).UpdateProduct(1, Edit(5900)));
        Assert.Equal(5900, (await db.Products.AsNoTracking().SingleAsync(Ct)).PriceInOre);
    }

    [Fact]
    public async Task Update_ADecrease_LowersTheProductAndTheSubscribersAtOnce()
    {
        using var db = await SeedAsync();

        Assert.IsType<OkObjectResult>(await Create(db).UpdateProduct(1, Edit(3900)));

        Assert.Equal(3900, (await db.Products.AsNoTracking().SingleAsync(Ct)).PriceInOre);
        Assert.Equal(3900, (await db.Subscriptions.AsNoTracking().SingleAsync(Ct)).UnitPriceInOre);
    }

    [Fact]
    public async Task SchedulePriceChange_ReturnsTheChange_AndListsIt()
    {
        using var db = await SeedAsync();
        var ctrl = Create(db);

        var ok = Assert.IsType<OkObjectResult>(await ctrl.SchedulePriceChange(1,
            new SchedulePriceChangeDto { NewPriceInOre = 5900, EffectiveFrom = DateTime.UtcNow.Date.AddDays(31) }));
        var dto = Assert.IsType<PriceChangeResponseDto>(ok.Value);
        Assert.Equal(4900, dto.OldPriceInOre);
        Assert.Equal(5900, dto.NewPriceInOre);

        var listed = Assert.IsType<List<PriceChangeResponseDto>>(Assert.IsType<OkObjectResult>(await ctrl.GetPriceChanges(1)).Value);
        Assert.Equal(dto.Id, Assert.Single(listed).Id);
    }

    [Fact]
    public async Task SchedulePriceChange_TooSoon_IsABadRequest()
    {
        using var db = await SeedAsync();

        Assert.IsType<BadRequestObjectResult>(await Create(db).SchedulePriceChange(1,
            new SchedulePriceChangeDto { NewPriceInOre = 5900, EffectiveFrom = DateTime.UtcNow.Date.AddDays(10) }));
    }

    [Fact]
    public async Task PriceChangeEndpoints_ForAnUnknownProduct_AreNotFound()
    {
        using var db = await SeedAsync();
        var ctrl = Create(db);

        Assert.IsType<NotFoundResult>(await ctrl.SchedulePriceChange(9, new SchedulePriceChangeDto { NewPriceInOre = 5900, EffectiveFrom = DateTime.UtcNow.AddDays(40) }));
        Assert.IsType<NotFoundResult>(await ctrl.GetPriceChanges(9));
    }

    [Fact]
    public async Task CancelPriceChange_UnderAnotherProduct_IsNotFound()
    {
        using var db = await SeedAsync();
        var ctrl = Create(db);
        var ok = Assert.IsType<OkObjectResult>(await ctrl.SchedulePriceChange(1,
            new SchedulePriceChangeDto { NewPriceInOre = 5900, EffectiveFrom = DateTime.UtcNow.Date.AddDays(31) }));
        var id = ((PriceChangeResponseDto)ok.Value!).Id;

        Assert.IsType<NotFoundResult>(await ctrl.CancelPriceChange(2, id));
        Assert.IsType<NoContentResult>(await ctrl.CancelPriceChange(1, id));
        Assert.IsType<ConflictObjectResult>(await ctrl.CancelPriceChange(1, id));
    }
}
