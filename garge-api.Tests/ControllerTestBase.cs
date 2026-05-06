using AutoMapper;
using garge_api.Controllers;
using garge_api.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moq;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace garge_api.Tests;

public abstract class ControllerTestBase
{
    protected const string TestJwtKey = "test-secret-key-that-is-long-enough-for-hmacsha256-algorithm!";
    protected const string TestJwtIssuer = "test-issuer";

    protected Mock<UserManager<User>> MockUserManager { get; }
    protected Mock<SignInManager<User>> MockSignInManager { get; }
    protected Mock<IEmailService> MockEmailService { get; } = new();
    protected Mock<IMapper> MockMapper { get; } = new();
    protected IConfiguration Configuration { get; }

    protected ControllerTestBase()
    {
        MockUserManager = CreateUserManagerMock();
        MockSignInManager = CreateSignInManagerMock(MockUserManager);
        Configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = TestJwtKey,
                ["Jwt:Issuer"] = TestJwtIssuer
            })
            .Build();
    }

    protected ApplicationDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    protected AuthController CreateAuthController(ApplicationDbContext? db = null) =>
        new(MockUserManager.Object, MockSignInManager.Object, Configuration,
            db ?? CreateDbContext(), MockEmailService.Object, MockMapper.Object,
            NullLogger<AuthController>.Instance);

    protected static AutomationController CreateAutomationController(
        ApplicationDbContext db, string userId = "user-1", bool isAdmin = false)
    {
        var controller = new AutomationController(db, NullLogger<AutomationController>.Instance);
        controller.ControllerContext = MakeControllerContext(userId, isAdmin);
        return controller;
    }

    protected static ControllerContext MakeControllerContext(string userId = "user-1", bool isAdmin = false)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        if (isAdmin)
            claims.Add(new(ClaimTypes.Role, "admin"));
        return new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
            }
        };
    }

    protected string GenerateTestJwt(string userId, bool expired = false)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtKey));
        var handler = new JwtSecurityTokenHandler();
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[] { new Claim(JwtRegisteredClaimNames.Sub, userId) }),
            NotBefore = expired ? DateTime.UtcNow.AddDays(-2) : DateTime.UtcNow.AddSeconds(-10),
            Expires = expired ? DateTime.UtcNow.AddDays(-1) : DateTime.UtcNow.AddDays(7),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256Signature),
            Issuer = TestJwtIssuer,
            Audience = TestJwtIssuer
        };
        return handler.WriteToken(handler.CreateToken(descriptor));
    }

    protected static User MakeUser(string id = "user-1", string email = "test@example.com") => new()
    {
        Id = id,
        Email = email,
        UserName = "testuser",
        FirstName = "Test",
        LastName = "User",
        EmailConfirmed = true
    };

    private static Mock<UserManager<User>> CreateUserManagerMock()
    {
        var store = new Mock<IUserStore<User>>();
        return new Mock<UserManager<User>>(
            store.Object,
            Mock.Of<IOptions<IdentityOptions>>(),
            Mock.Of<IPasswordHasher<User>>(),
            Array.Empty<IUserValidator<User>>(),
            Array.Empty<IPasswordValidator<User>>(),
            Mock.Of<ILookupNormalizer>(),
            new IdentityErrorDescriber(),
            Mock.Of<IServiceProvider>(),
            NullLogger<UserManager<User>>.Instance);
    }

    private static Mock<SignInManager<User>> CreateSignInManagerMock(Mock<UserManager<User>> um) =>
        new(um.Object,
            Mock.Of<IHttpContextAccessor>(),
            Mock.Of<IUserClaimsPrincipalFactory<User>>(),
            Mock.Of<IOptions<IdentityOptions>>(),
            NullLogger<SignInManager<User>>.Instance,
            Mock.Of<IAuthenticationSchemeProvider>(),
            Mock.Of<IUserConfirmation<User>>());
}
