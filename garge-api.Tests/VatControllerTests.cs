using System.Text;
using garge_api.Controllers;
using garge_api.Models;
using garge_api.Models.Admin;
using garge_api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace garge_api.Tests;

public class VatControllerTests
{
    private static ApplicationDbContext Db()
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.AppSettings.Add(new AppSettings { Id = 1 });
        db.SaveChanges();
        return db;
    }

    private static Mock<IAppSettingsCache> Cache(bool vatEnabled)
    {
        var cache = new Mock<IAppSettingsCache>();
        cache.Setup(c => c.GetAsync()).ReturnsAsync(new AppSettings { Id = 1, VatEnabled = vatEnabled });
        return cache;
    }

    private static VatThresholdStatus Status(params VatOwedSale[] owed) =>
        new(5_100_000, 0, VatThresholdService.ThresholdInOre, false, owed.FirstOrDefault()?.InvoiceId, owed.FirstOrDefault()?.IssuedAt, owed);

    [Fact]
    public async Task MakeSupplements_WithVatOff_IsRefused()
    {
        var invoices = new Mock<IInvoiceService>();
        var ctrl = new VatController(Db(), new Mock<IVatThresholdService>().Object, invoices.Object, Cache(false).Object);

        Assert.IsType<BadRequestObjectResult>(await ctrl.MakeSupplements(CancellationToken.None));
        invoices.Verify(i => i.GenerateVatSupplementsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetOtherTurnover_SavesItAndChecksTheThreshold()
    {
        using var db = Db();
        var vat = new Mock<IVatThresholdService>();
        var ctrl = new VatController(db, vat.Object, new Mock<IInvoiceService>().Object, Cache(false).Object);

        Assert.IsType<NoContentResult>(await ctrl.SetOtherTurnover(new() { OtherTurnoverInOre = 1_234_500 }, CancellationToken.None));

        Assert.Equal(1_234_500, (await db.AppSettings.SingleAsync(TestContext.Current.CancellationToken)).OtherTurnoverInOre);
        vat.Verify(v => v.CheckAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetThreshold_SumsTheVatOwed()
    {
        var vat = new Mock<IVatThresholdService>();
        vat.Setup(v => v.GetStatusAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(Status(
            new VatOwedSale(7, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), 200_000, 40_000, null, null),
            new VatOwedSale(8, new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc), 20_000, 4_000, null, null)));
        var ctrl = new VatController(Db(), vat.Object, new Mock<IInvoiceService>().Object, Cache(false).Object);

        var dto = (await ctrl.GetThreshold(CancellationToken.None)).Value!;

        Assert.Equal(44_000, dto.OwedVatInOre);
        Assert.Equal(7, dto.CrossingInvoiceId);
        Assert.Equal([7, 8], dto.Owed.Select(o => o.InvoiceId));
    }

    [Fact]
    public async Task OwedCsv_ListsEachSaleWithTheVatTakenOut()
    {
        var vat = new Mock<IVatThresholdService>();
        vat.Setup(v => v.GetStatusAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(Status(
            new VatOwedSale(7, new DateTime(2026, 8, 31, 23, 30, 0, DateTimeKind.Utc), 200_000, 40_000, null, null)));
        var ctrl = new VatController(Db(), vat.Object, new Mock<IInvoiceService>().Object, Cache(false).Object);

        var file = Assert.IsType<FileContentResult>(await ctrl.GetOwedCsv(CancellationToken.None));

        // 23:30 UTC on 31 August is 1 September in Norway.
        Assert.Equal("invoice;date;amount_nok;excl_vat_nok;vat_nok;supplement\n7;2026-09-01;2000.00;1600.00;400.00;\n", Encoding.UTF8.GetString(file.FileContents));
    }

    [Fact]
    public async Task GetSupplement_WithoutOne_IsNotFound()
    {
        var ctrl = new VatController(Db(), new Mock<IVatThresholdService>().Object, new Mock<IInvoiceService>().Object, Cache(true).Object);
        Assert.IsType<NotFoundResult>(await ctrl.GetSupplement(42, CancellationToken.None));
    }

    [Fact]
    public void EveryEndpoint_IsAdminOnly()
    {
        var policy = Assert.Single(typeof(VatController).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()).Policy;
        Assert.Equal("Admin", policy);
        Assert.DoesNotContain(typeof(VatController).GetMethods(),
            m => m.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), true).Length > 0);
    }
}
