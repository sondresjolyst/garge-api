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
                dst.CookieBannerEnabled = src.CookieBannerEnabled;
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
        await db.SaveChangesAsync();

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
        Assert.Equal(1, await db.AppSettings.CountAsync());
    }

    [Fact]
    public async Task UpdateAppSettings_ExistingRow_UpdatesInPlaceWithoutDuplicate()
    {
        var db = CreateDbContext();
        db.AppSettings.Add(new AppSettings { Id = 1, CookieBannerEnabled = true });
        await db.SaveChangesAsync();

        await CreateAdminController(db).UpdateAppSettings(
            new UpdateAppSettingsDto { CookieBannerEnabled = false });

        var settings = await db.AppSettings.FindAsync(1);
        Assert.False(settings!.CookieBannerEnabled);
        Assert.Equal(1, await db.AppSettings.CountAsync());
    }
}
