using AutoMapper;
using garge_api.Controllers;
using garge_api.Dtos.Admin;
using garge_api.Models;
using garge_api.Models.Admin;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace garge_api.Tests;

public class AdminControllerTests : ControllerTestBase
{
    private static Mock<RoleManager<IdentityRole>> CreateRoleManagerMock()
    {
        var store = new Mock<IRoleStore<IdentityRole>>();
        return new Mock<RoleManager<IdentityRole>>(
            store.Object,
            Array.Empty<IRoleValidator<IdentityRole>>(),
            Mock.Of<ILookupNormalizer>(),
            new IdentityErrorDescriber(),
            NullLogger<RoleManager<IdentityRole>>.Instance);
    }

    private AdminController CreateAdminController(ApplicationDbContext db, bool isAdmin = true)
    {
        MockMapper.Setup(m => m.Map<AppSettingsDto>(It.IsAny<AppSettings>()))
            .Returns((AppSettings s) => new AppSettingsDto { CookieBannerEnabled = s.CookieBannerEnabled });

        MockMapper.Setup(m => m.Map<UpdateAppSettingsDto, AppSettings>(
                It.IsAny<UpdateAppSettingsDto>(), It.IsAny<AppSettings>()))
            .Returns<UpdateAppSettingsDto, AppSettings>((src, dst) =>
            {
                if (src.CookieBannerEnabled.HasValue) dst.CookieBannerEnabled = src.CookieBannerEnabled.Value;
                return dst;
            });

        var controller = new AdminController(
            CreateRoleManagerMock().Object,
            MockUserManager.Object,
            db,
            NullLogger<AdminController>.Instance,
            MockMapper.Object,
            MockEmailService.Object);
        controller.ControllerContext = MakeControllerContext(isAdmin: isAdmin);
        return controller;
    }

    [Fact]
    public async Task GetAppSettings_NoRowInDb_ReturnsDefaults()
    {
        var db = CreateDbContext();

        var result = await CreateAdminController(db).GetAppSettings();

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<AppSettingsDto>(ok.Value);
        Assert.True(dto.CookieBannerEnabled);
    }

    [Fact]
    public async Task GetAppSettings_RowExists_ReturnsStoredValue()
    {
        var db = CreateDbContext();
        db.AppSettings.Add(new AppSettings { Id = 1, CookieBannerEnabled = false });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await CreateAdminController(db).GetAppSettings();

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<AppSettingsDto>(ok.Value);
        Assert.False(dto.CookieBannerEnabled);
    }

    [Fact]
    public async Task UpdateAppSettings_NoExistingRow_CreatesRowAndReturnsUpdated()
    {
        var db = CreateDbContext();

        var result = await CreateAdminController(db).UpdateAppSettings(
            new UpdateAppSettingsDto { CookieBannerEnabled = false });

        var ok = Assert.IsType<OkObjectResult>(result);
        var returned = Assert.IsType<AppSettingsDto>(ok.Value);
        Assert.False(returned.CookieBannerEnabled);
        Assert.Equal(1, await db.AppSettings.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateAppSettings_ExistingRow_UpdatesInPlaceWithoutDuplicate()
    {
        var db = CreateDbContext();
        db.AppSettings.Add(new AppSettings { Id = 1, CookieBannerEnabled = true });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await CreateAdminController(db).UpdateAppSettings(
            new UpdateAppSettingsDto { CookieBannerEnabled = false });

        var settings = await db.AppSettings.FindAsync([1], TestContext.Current.CancellationToken);
        Assert.False(settings!.CookieBannerEnabled);
        Assert.Equal(1, await db.AppSettings.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateAppSettings_PartialUpdate_DoesNotWipeOtherFields()
    {
        var db = CreateDbContext();
        db.AppSettings.Add(new AppSettings
        {
            Id = 1,
            CookieBannerEnabled = true,
            CompanyName = "Garge AS",
            CompanyOrgNumber = "934 531 035",
            CompanyEmail = "hello@garge.no",
            VippsShopWebhookSecret = "must-not-disappear"
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        MockMapper.Setup(m => m.Map<UpdateAppSettingsDto, AppSettings>(
                It.IsAny<UpdateAppSettingsDto>(), It.IsAny<AppSettings>()))
            .Returns<UpdateAppSettingsDto, AppSettings>((src, dst) =>
            {
                if (src.CookieBannerEnabled.HasValue) dst.CookieBannerEnabled = src.CookieBannerEnabled.Value;
                if (src.VatEnabled.HasValue) dst.VatEnabled = src.VatEnabled.Value;
                if (src.VippsTestMode.HasValue) dst.VippsTestMode = src.VippsTestMode.Value;
                if (src.CompanyName != null) dst.CompanyName = src.CompanyName;
                if (src.CompanyOrgNumber != null) dst.CompanyOrgNumber = src.CompanyOrgNumber;
                if (src.CompanyEmail != null) dst.CompanyEmail = src.CompanyEmail;
                return dst;
            });

        await CreateAdminController(db).UpdateAppSettings(
            new UpdateAppSettingsDto { CookieBannerEnabled = false });

        var settings = await db.AppSettings.FindAsync([1], TestContext.Current.CancellationToken);
        Assert.False(settings!.CookieBannerEnabled);
        Assert.Equal("Garge AS", settings.CompanyName);
        Assert.Equal("934 531 035", settings.CompanyOrgNumber);
        Assert.Equal("hello@garge.no", settings.CompanyEmail);
        Assert.Equal("must-not-disappear", settings.VippsShopWebhookSecret);
    }
}
