using System.Text;
using garge_api.Controllers;
using garge_api.Dtos.Admin;
using garge_api.Models;
using garge_api.Models.Admin;
using garge_api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SjolystInnovation.AppKit.VatThreshold;
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
        new(5_100_000, VatThresholdService.ThresholdInOre, false, owed.FirstOrDefault()?.InvoiceId, owed.FirstOrDefault()?.IssuedAt, owed,
            [new VatTurnoverMonth(new DateOnly(2025, 10, 1), 100_000, 0), new VatTurnoverMonth(new DateOnly(2026, 9, 1), 4_900_000, 100_000)]);

    // ValidationProblem needs the MVC services, as it has in the app.
    private static VatController WithHttpContext(VatController ctrl)
    {
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { RequestServices = new ServiceCollection().AddControllers().Services.BuildServiceProvider() }
        };
        return ctrl;
    }

    [Fact]
    public async Task MakeCorrections_WithVatOff_IsRefused()
    {
        var invoices = new Mock<IInvoiceService>();
        var ctrl = new VatController(Db(), new Mock<IVatThresholdService>().Object, invoices.Object, Cache(false).Object);

        Assert.IsType<BadRequestObjectResult>(await ctrl.MakeCorrections(CancellationToken.None));
        invoices.Verify(i => i.GenerateVatCorrectionsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetOutsideSales_StoresTheMonthAndAnswersTheThreshold()
    {
        var vat = new Mock<IVatThresholdService>();
        vat.Setup(v => v.SetOutsideSalesAsync(It.IsAny<DateOnly>(), It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        vat.Setup(v => v.GetStatusAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(Status());
        var ctrl = new VatController(Db(), vat.Object, new Mock<IInvoiceService>().Object, Cache(false).Object);

        var dto = (await ctrl.SetOutsideSales(new() { Month = "2026-09", AmountInOre = 1_234_500 }, CancellationToken.None)).Value!;

        vat.Verify(v => v.SetOutsideSalesAsync(new DateOnly(2026, 9, 1), 1_234_500, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(5_100_000, dto.TurnoverInOre);
        Assert.Equal(2, dto.Months.Count);
    }

    [Theory]
    [InlineData("The month has not started yet.")]
    [InlineData("The month is more than 36 months back.")]
    public async Task SetOutsideSales_AMonthTheServiceRefuses_Answers400WithItsReasonOnTheMonth(string reason)
    {
        var vat = new Mock<IVatThresholdService>();
        vat.Setup(v => v.SetOutsideSalesAsync(It.IsAny<DateOnly>(), It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(reason);
        var ctrl = WithHttpContext(new VatController(Db(), vat.Object, new Mock<IInvoiceService>().Object, Cache(false).Object));

        var result = (await ctrl.SetOutsideSales(new() { Month = "2099-01", AmountInOre = 10_000 }, CancellationToken.None)).Result;

        var problem = Assert.IsType<ValidationProblemDetails>(Assert.IsType<BadRequestObjectResult>(result).Value);
        Assert.Equal([reason], problem.Errors["Month"]);
    }

    [Theory]
    [InlineData("2026-13")]
    [InlineData("2026-9")]
    [InlineData("1999-12")]
    [InlineData("2026-09-01")]
    public async Task SetOutsideSales_NotAMonth_IsRefusedWithoutStoring(string month)
    {
        var vat = new Mock<IVatThresholdService>();
        var ctrl = WithHttpContext(new VatController(Db(), vat.Object, new Mock<IInvoiceService>().Object, Cache(false).Object));
        var dto = new UpdateOutsideSalesDto { Month = month, AmountInOre = 10_000 };

        var result = (await ctrl.SetOutsideSales(dto, CancellationToken.None)).Result;

        Assert.IsType<ValidationProblemDetails>(Assert.IsType<BadRequestObjectResult>(result).Value);
        Assert.NotEmpty(dto.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(dto)));
        vat.Verify(v => v.SetOutsideSalesAsync(It.IsAny<DateOnly>(), It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1_000_000_000, true)]
    [InlineData(1_000_000_001, false)]
    public void UpdateOutsideSalesDto_AmountIsZeroUpTo10MillionKroner(long amount, bool valid)
    {
        var dto = new UpdateOutsideSalesDto { Month = "2026-09", AmountInOre = amount };

        Assert.Equal(valid, System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
            dto, new System.ComponentModel.DataAnnotations.ValidationContext(dto), null, validateAllProperties: true));
    }

    [Fact]
    public async Task GetThreshold_SumsTheVatOwed()
    {
        var vat = new Mock<IVatThresholdService>();
        vat.Setup(v => v.GetStatusAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(Status(
            new VatOwedSale(7, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), 200_000, 40_000, null, null, null),
            new VatOwedSale(8, new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc), 20_000, 4_000, null, null, null)));
        var ctrl = new VatController(Db(), vat.Object, new Mock<IInvoiceService>().Object, Cache(false).Object);

        var dto = (await ctrl.GetThreshold(CancellationToken.None)).Value!;

        Assert.Equal(44_000, dto.OwedVatInOre);
        Assert.Equal(7, dto.CrossingInvoiceId);
        Assert.Equal([7, 8], dto.Owed.Select(o => o.InvoiceId));
        Assert.Equal(["2025-10", "2026-09"], dto.Months.Select(m => m.Month));
        Assert.Equal((4_900_000L, 100_000L), (dto.Months[1].SalesInOre, dto.Months[1].OutsideInOre));
    }

    [Fact]
    public async Task OwedCsv_ListsEachSaleWithTheVatTakenOut()
    {
        var vat = new Mock<IVatThresholdService>();
        vat.Setup(v => v.GetStatusAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(Status(
            new VatOwedSale(7, new DateTime(2026, 8, 31, 23, 30, 0, DateTimeKind.Utc), 200_000, 40_000, null, null, null)));
        var ctrl = new VatController(Db(), vat.Object, new Mock<IInvoiceService>().Object, Cache(false).Object);

        var file = Assert.IsType<FileContentResult>(await ctrl.GetOwedCsv(CancellationToken.None));

        // 23:30 UTC on 31 August is 1 September in Norway.
        Assert.Equal("invoice;date;amount_nok;excl_vat_nok;vat_nok;credit_note;new_invoice\n7;2026-09-01;2000.00;1600.00;400.00;;\n", Encoding.UTF8.GetString(file.FileContents));
    }

    [Fact]
    public async Task GetDocument_OnlyServesCorrectionDocuments()
    {
        using var db = Db();
        var order = new garge_api.Models.Shop.Order { UserId = "u", TotalInOre = 100 };
        db.Orders.Add(order);
        db.SaveChanges();
        var regular = new garge_api.Models.Shop.Invoice { OrderId = order.Id, AmountInOre = 100, PdfData = [1] };
        var credit = new garge_api.Models.Shop.Invoice { Kind = garge_api.Models.Shop.InvoiceKind.CreditNote, CreditsInvoiceId = 1, AmountInOre = -100, PdfData = [2] };
        db.Invoices.AddRange(regular, credit);
        db.SaveChanges();
        var ctrl = new VatController(db, new Mock<IVatThresholdService>().Object, new Mock<IInvoiceService>().Object, Cache(true).Object);

        Assert.IsType<NotFoundResult>(await ctrl.GetDocument(regular.Id, CancellationToken.None));
        var file = Assert.IsType<FileContentResult>(await ctrl.GetDocument(credit.Id, CancellationToken.None));
        Assert.Equal($"credit-note-{credit.Id:D4}.pdf", file.FileDownloadName);
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
