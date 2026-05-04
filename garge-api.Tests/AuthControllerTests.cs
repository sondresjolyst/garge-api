using garge_api.Dtos.Auth;
using garge_api.Models.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SignInResult = Microsoft.AspNetCore.Identity.SignInResult;
using Moq;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace garge_api.Tests;

public class AuthControllerTests : ControllerTestBase
{
    [Fact]
    public async Task Login_MissingEmail_ReturnsBadRequest()
    {
        var result = await CreateAuthController().Login(new LoginModel { Email = "", Password = "pass" });
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Login_MissingPassword_ReturnsBadRequest()
    {
        var result = await CreateAuthController().Login(new LoginModel { Email = "test@test.com", Password = "" });
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Login_UserNotFound_ReturnsUnauthorized()
    {
        MockUserManager.Setup(m => m.FindByEmailAsync(It.IsAny<string>())).ReturnsAsync((User?)null);

        var result = await CreateAuthController().Login(new LoginModel { Email = "x@x.com", Password = "pass" });

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task Login_InvalidPassword_ReturnsUnauthorized()
    {
        var user = MakeUser();
        MockUserManager.Setup(m => m.FindByEmailAsync(user.Email!)).ReturnsAsync(user);
        MockSignInManager
            .Setup(m => m.PasswordSignInAsync(user.UserName!, It.IsAny<string>(), false, true))
            .ReturnsAsync(SignInResult.Failed);

        var result = await CreateAuthController().Login(new LoginModel { Email = user.Email!, Password = "wrong" });

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task Login_ValidCredentials_ReturnsOkWithTokenAndRefreshToken()
    {
        var user = MakeUser();
        var db = CreateDbContext();
        MockUserManager.Setup(m => m.FindByEmailAsync(user.Email!)).ReturnsAsync(user);
        MockSignInManager
            .Setup(m => m.PasswordSignInAsync(user.UserName!, It.IsAny<string>(), false, true))
            .ReturnsAsync(SignInResult.Success);
        MockUserManager.Setup(m => m.GetRolesAsync(user)).ReturnsAsync(new List<string> { "Default" });

        var result = await CreateAuthController(db).Login(new LoginModel { Email = user.Email!, Password = "pass" });

        var ok = Assert.IsType<OkObjectResult>(result);
        var token = ok.Value!.GetType().GetProperty("token")?.GetValue(ok.Value) as string;
        var refreshToken = ok.Value!.GetType().GetProperty("refreshToken")?.GetValue(ok.Value) as string;
        Assert.NotNull(token);
        Assert.NotNull(refreshToken);
        Assert.Single(db.RefreshTokens);
    }

    [Fact]
    public async Task Login_AtRefreshTokenLimit_RemovesOldestBeforeAdding()
    {
        var user = MakeUser();
        var db = CreateDbContext();

        for (int i = 0; i < 5; i++)
        {
            db.RefreshTokens.Add(new RefreshToken
            {
                Token = $"token-{i}",
                UserId = user.Id,
                Expires = DateTime.UtcNow.AddMonths(1),
                Created = DateTime.UtcNow.AddMinutes(-i)
            });
        }
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        MockUserManager.Setup(m => m.FindByEmailAsync(user.Email!)).ReturnsAsync(user);
        MockSignInManager
            .Setup(m => m.PasswordSignInAsync(user.UserName!, It.IsAny<string>(), false, true))
            .ReturnsAsync(SignInResult.Success);
        MockUserManager.Setup(m => m.GetRolesAsync(user)).ReturnsAsync(new List<string>());

        await CreateAuthController(db).Login(new LoginModel { Email = user.Email!, Password = "pass" });

        Assert.Equal(5, db.RefreshTokens.Count());
    }

    [Fact]
    public async Task RefreshToken_InvalidJwt_ReturnsBadRequest()
    {
        var result = await CreateAuthController().RefreshToken(
            new RefreshTokenRequestDto { Token = "not-a-jwt", RefreshToken = "anything" });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task RefreshToken_ExpiredRefreshToken_ReturnsUnauthorized()
    {
        var user = MakeUser();
        var db = CreateDbContext();
        var jwt = GenerateTestJwt(user.Id, expired: true);
        MockUserManager.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);

        var result = await CreateAuthController(db).RefreshToken(
            new RefreshTokenRequestDto { Token = jwt, RefreshToken = "no-such-token" });

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task RefreshToken_ValidToken_ReturnsOkWithNewTokens()
    {
        var user = MakeUser();
        var db = CreateDbContext();
        var jwt = GenerateTestJwt(user.Id, expired: true);

        var rawRefreshToken = "raw-refresh-token";
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(rawRefreshToken)));
        db.RefreshTokens.Add(new RefreshToken
        {
            Token = hash,
            UserId = user.Id,
            Expires = DateTime.UtcNow.AddMonths(1),
            Created = DateTime.UtcNow
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        MockUserManager.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        MockUserManager.Setup(m => m.GetRolesAsync(user)).ReturnsAsync(new List<string>());

        var result = await CreateAuthController(db).RefreshToken(
            new RefreshTokenRequestDto { Token = jwt, RefreshToken = rawRefreshToken });

        var ok = Assert.IsType<OkObjectResult>(result);
        var newToken = ok.Value!.GetType().GetProperty("token")?.GetValue(ok.Value) as string;
        Assert.NotNull(newToken);
        Assert.Equal(1, db.RefreshTokens.Count(t => t.Revoked == null));
    }

    [Fact]
    public async Task Register_MissingEmail_ReturnsBadRequest()
    {
        var result = await CreateAuthController().Register(new RegisterUserDto
        {
            Email = "",
            UserName = "user",
            Password = "pass",
            FirstName = "First",
            LastName = "Last"
        });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Register_EmailAlreadyExists_ReturnsConflict()
    {
        MockUserManager.Setup(m => m.FindByEmailAsync("exists@test.com")).ReturnsAsync(MakeUser());

        var result = await CreateAuthController().Register(new RegisterUserDto
        {
            Email = "exists@test.com",
            UserName = "user",
            Password = "pass",
            FirstName = "First",
            LastName = "Last"
        });

        Assert.IsType<ConflictObjectResult>(result);
    }
}
