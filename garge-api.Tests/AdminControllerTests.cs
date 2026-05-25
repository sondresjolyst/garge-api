using MapsterMapper;
using garge_api.Controllers;
using garge_api.Dtos.Admin;
using garge_api.Models;
using garge_api.Models.Admin;
using garge_api.Models.Shop;
using garge_api.Models.Subscription;
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

    [Fact]
    public async Task GetStats_PopulatesOrderStats()
    {
        var db = CreateDbContext();
        MockUserManager.Setup(m => m.Users).Returns(db.Users);

        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        db.Orders.AddRange(
            new Order { UserId = "u1", Status = OrderStatus.Paid, TotalInOre = 50000, CreatedAt = DateTime.UtcNow },
            new Order { UserId = "u1", Status = OrderStatus.Paid, TotalInOre = 30000, CreatedAt = DateTime.UtcNow },
            new Order { UserId = "u1", Status = OrderStatus.Paid, TotalInOre = 12000, CreatedAt = monthStart.AddMonths(-2) },
            new Order { UserId = "u1", Status = OrderStatus.Reserved, TotalInOre = 9900, CreatedAt = DateTime.UtcNow },
            new Order { UserId = "u1", Status = OrderStatus.Failed, TotalInOre = 0, CreatedAt = DateTime.UtcNow },
            new Order { UserId = "u1", Status = OrderStatus.Cancelled, TotalInOre = 0, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await CreateAdminController(db).GetStats();

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<AdminStatsDto>(ok.Value);
        Assert.Equal(5, dto.Orders.Today);                 // 5 created today (paid x2, reserved, failed, cancelled)
        Assert.Equal(1, dto.Orders.PendingCapture);
        Assert.Equal(2, dto.Orders.FailedOrCancelled);
        Assert.Equal(92000L, dto.Orders.TotalRevenueInOre); // 50000 + 30000 + 12000
        Assert.Equal(80000L, dto.Orders.MonthRevenueInOre); // 50000 + 30000 (12000 was 2 months ago)
    }

    [Fact]
    public async Task GetStats_PopulatesSubscriptionStats_AndComputesMrr()
    {
        var db = CreateDbContext();
        MockUserManager.Setup(m => m.Users).Returns(db.Users);

        var monthly = new Product { Id = 1, Name = "Garge Basic", PriceInOre = 29900, Interval = BillingInterval.Monthly, Type = ProductType.Primary, IsActive = true };
        var yearly = new Product { Id = 2, Name = "Garge Yearly", PriceInOre = 299000, Interval = BillingInterval.Yearly, Type = ProductType.Primary, IsActive = true };
        db.Products.AddRange(monthly, yearly);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.Subscriptions.AddRange(
            new Subscription { UserId = "u1", ProductId = monthly.Id, VippsAgreementId = "agr-1", Status = SubscriptionStatus.Active },
            new Subscription { UserId = "u2", ProductId = monthly.Id, VippsAgreementId = "agr-2", Status = SubscriptionStatus.Active },
            new Subscription { UserId = "u3", ProductId = yearly.Id, VippsAgreementId = "agr-3", Status = SubscriptionStatus.Active },
            new Subscription { UserId = "u4", ProductId = monthly.Id, VippsAgreementId = "agr-4", Status = SubscriptionStatus.Pending });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await CreateAdminController(db).GetStats();

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<AdminStatsDto>(ok.Value);
        Assert.Equal(3, dto.Subscriptions.Active);
        Assert.Equal(1, dto.Subscriptions.PendingConfirm);
        // 2 * 29900 (monthly) + 299000 / 12 (yearly normalized) = 59800 + 24916 = 84716
        Assert.Equal(29900L * 2 + 299000L / 12, dto.Subscriptions.MonthlyRecurringInOre);
    }

    [Fact]
    public async Task GetStats_DefaultExcludesTestData()
    {
        var db = CreateDbContext();
        MockUserManager.Setup(m => m.Users).Returns(db.Users);

        db.Orders.AddRange(
            new Order { UserId = "u1", Status = OrderStatus.Paid, TotalInOre = 1000, CreatedAt = DateTime.UtcNow, IsTest = false },
            new Order { UserId = "u1", Status = OrderStatus.Paid, TotalInOre = 9999, CreatedAt = DateTime.UtcNow, IsTest = true });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await CreateAdminController(db).GetStats();

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<AdminStatsDto>(ok.Value);
        Assert.Equal(1, dto.Orders.Today);
        Assert.Equal(1000L, dto.Orders.TotalRevenueInOre);
    }

    [Fact]
    public async Task GetStats_TestTrue_OnlyReturnsTestData()
    {
        var db = CreateDbContext();
        MockUserManager.Setup(m => m.Users).Returns(db.Users);

        db.Orders.AddRange(
            new Order { UserId = "u1", Status = OrderStatus.Paid, TotalInOre = 1000, CreatedAt = DateTime.UtcNow, IsTest = false },
            new Order { UserId = "u1", Status = OrderStatus.Paid, TotalInOre = 9999, CreatedAt = DateTime.UtcNow, IsTest = true });

        var product = new Product { Id = 1, Name = "P", PriceInOre = 5000, Interval = BillingInterval.Monthly, Type = ProductType.Primary, IsActive = true };
        db.Products.Add(product);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.Subscriptions.AddRange(
            new Subscription { UserId = "u1", ProductId = 1, VippsAgreementId = "live", Status = SubscriptionStatus.Active, IsTest = false },
            new Subscription { UserId = "u2", ProductId = 1, VippsAgreementId = "test", Status = SubscriptionStatus.Active, IsTest = true });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await CreateAdminController(db).GetStats(test: true);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<AdminStatsDto>(ok.Value);
        Assert.Equal(1, dto.Orders.Today);
        Assert.Equal(9999L, dto.Orders.TotalRevenueInOre);
        Assert.Equal(1, dto.Subscriptions.Active);
        Assert.Equal(5000L, dto.Subscriptions.MonthlyRecurringInOre);
    }

    [Fact]
    public async Task GetStats_StoppedThisMonth_OnlyCountsCurrentMonth()
    {
        var db = CreateDbContext();
        MockUserManager.Setup(m => m.Users).Returns(db.Users);

        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var product = new Product { Id = 1, Name = "X", PriceInOre = 10000, Interval = BillingInterval.Monthly, Type = ProductType.Primary, IsActive = true };
        db.Products.Add(product);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.Subscriptions.AddRange(
            new Subscription { UserId = "u1", ProductId = 1, VippsAgreementId = "agr-old", Status = SubscriptionStatus.Stopped, UpdatedAt = monthStart.AddMonths(-1) },
            new Subscription { UserId = "u2", ProductId = 1, VippsAgreementId = "agr-new", Status = SubscriptionStatus.Stopped, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await CreateAdminController(db).GetStats();

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<AdminStatsDto>(ok.Value);
        Assert.Equal(1, dto.Subscriptions.StoppedThisMonth);
    }

    private static User MakeUser(string id, DateTime createdAt, bool deleted = false, DateTime? deletedAt = null) => new()
    {
        Id = id, UserName = id, Email = $"{id}@test.com", FirstName = "Test", LastName = "User",
        CreatedAt = createdAt, IsDeleted = deleted, DeletedAt = deletedAt,
    };

    private void SetupUserMapAndRoles()
    {
        MockMapper.Setup(m => m.Map<UserDto>(It.IsAny<User>()))
            .Returns((User u) => new UserDto
            {
                Id = u.Id!, UserName = u.UserName ?? "", FirstName = u.FirstName, LastName = u.LastName,
                Email = u.Email ?? "", IsDeleted = u.IsDeleted,
            });
        MockUserManager.Setup(m => m.GetRolesAsync(It.IsAny<User>())).ReturnsAsync(new List<string>());
    }

    private static int TotalUsersOn(List<object> rows, DateTime date)
    {
        var key = date.ToString("yyyy-MM-dd");
        var row = rows.Single(r => (string)r.GetType().GetProperty("date")!.GetValue(r)! == key);
        return (int)row.GetType().GetProperty("totalUsers")!.GetValue(row)!;
    }

    [Fact]
    public async Task GetStats_TotalUsers_ExcludesDeleted()
    {
        var db = CreateDbContext();
        MockUserManager.Setup(m => m.Users).Returns(db.Users);
        db.Users.AddRange(
            MakeUser("u1", DateTime.UtcNow),
            MakeUser("u2", DateTime.UtcNow),
            MakeUser("u3", DateTime.UtcNow, deleted: true, deletedAt: DateTime.UtcNow));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await CreateAdminController(db).GetStats();

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<AdminStatsDto>(ok.Value);
        Assert.Equal(2, dto.TotalUsers);
    }

    [Fact]
    public async Task GetStatsHistory_SubtractsDeletionsOnDeletedDate()
    {
        // Three users sign up on d1; one deletes on d2. The running total climbs to 3, then dips to 2.
        var db = CreateDbContext();
        MockUserManager.Setup(m => m.Users).Returns(db.Users);
        var today = DateTime.UtcNow.Date;
        var d1 = today.AddDays(-3);
        var d2 = today.AddDays(-2);
        db.Users.AddRange(
            MakeUser("u1", d1),
            MakeUser("u2", d1),
            MakeUser("u3", d1, deleted: true, deletedAt: d2));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await CreateAdminController(db).GetStatsHistory();

        var ok = Assert.IsType<OkObjectResult>(result);
        var rows = Assert.IsType<List<object>>(ok.Value);
        Assert.Equal(3, TotalUsersOn(rows, d1));
        Assert.Equal(2, TotalUsersOn(rows, d2));
        Assert.Equal(2, TotalUsersOn(rows, today));
    }

    [Fact]
    public async Task GetUsers_HidesDeletedByDefault()
    {
        var db = CreateDbContext();
        SetupUserMapAndRoles();
        MockUserManager.Setup(m => m.Users).Returns(db.Users);
        db.Users.AddRange(
            MakeUser("u1", DateTime.UtcNow),
            MakeUser("u2", DateTime.UtcNow, deleted: true, deletedAt: DateTime.UtcNow));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await CreateAdminController(db).GetUsers();

        var ok = Assert.IsType<OkObjectResult>(result);
        var dtos = Assert.IsType<List<UserDto>>(ok.Value);
        Assert.Equal("u1", Assert.Single(dtos).Id);
    }

    [Fact]
    public async Task GetUsers_IncludeDeleted_ReturnsAllWithFlag()
    {
        var db = CreateDbContext();
        SetupUserMapAndRoles();
        MockUserManager.Setup(m => m.Users).Returns(db.Users);
        db.Users.AddRange(
            MakeUser("u1", DateTime.UtcNow),
            MakeUser("u2", DateTime.UtcNow, deleted: true, deletedAt: DateTime.UtcNow));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await CreateAdminController(db).GetUsers(includeDeleted: true);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dtos = Assert.IsType<List<UserDto>>(ok.Value);
        Assert.Equal(2, dtos.Count);
        Assert.Contains(dtos, d => d.Id == "u2" && d.IsDeleted);
    }

    [Fact]
    public async Task AssignPermission_UnknownPermission_Returns400()
    {
        var db = CreateDbContext();
        var roleManager = CreateRoleManagerMock();
        roleManager.Setup(r => r.RoleExistsAsync("Default")).ReturnsAsync(true);

        var controller = new AdminController(
            roleManager.Object, MockUserManager.Object, db,
            NullLogger<AdminController>.Instance, MockMapper.Object, MockEmailService.Object);
        controller.ControllerContext = MakeControllerContext(isAdmin: true);

        var result = await controller.AssignPermission("Default", "TotallyMadeUpPermission");

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(db.RolePermissions);
    }

    [Fact]
    public async Task AssignPermission_AlreadyAssigned_DoesNotInsertDuplicate()
    {
        var db = CreateDbContext();
        db.RolePermissions.Add(new RolePermission { RoleName = "Default", Permission = "Electricity" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var roleManager = CreateRoleManagerMock();
        roleManager.Setup(r => r.RoleExistsAsync("Default")).ReturnsAsync(true);

        var controller = new AdminController(
            roleManager.Object, MockUserManager.Object, db,
            NullLogger<AdminController>.Instance, MockMapper.Object, MockEmailService.Object);
        controller.ControllerContext = MakeControllerContext(isAdmin: true);

        var result = await controller.AssignPermission("Default", "Electricity");

        Assert.IsType<OkObjectResult>(result);
        Assert.Single(db.RolePermissions);
    }
}
