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

    private static RegisterUserDto MakeRegisterDto(string email = "user@test.com") => new()
    {
        Email = email,
        UserName = "user",
        Password = "pass",
        FirstName = "First",
        LastName = "Last",
        ConfirmAge16Plus = true,
        AcceptTerms = true,
        TermsVersion = "v1-test"
    };

    [Fact]
    public async Task Register_MissingEmail_ReturnsBadRequest()
    {
        var dto = MakeRegisterDto(email: "");
        var result = await CreateAuthController().Register(dto);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Register_AgeNotConfirmed_ReturnsBadRequest()
    {
        var dto = MakeRegisterDto();
        dto.ConfirmAge16Plus = false;
        var result = await CreateAuthController().Register(dto);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Register_TermsNotAccepted_ReturnsBadRequest()
    {
        var dto = MakeRegisterDto();
        dto.AcceptTerms = false;
        var result = await CreateAuthController().Register(dto);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Login_SoftDeletedUser_ReturnsUnauthorized()
    {
        var user = MakeUser();
        user.IsDeleted = true;
        MockUserManager.Setup(m => m.FindByEmailAsync(user.Email!)).ReturnsAsync(user);

        var result = await CreateAuthController().Login(new LoginModel { Email = user.Email!, Password = "pass" });

        Assert.IsType<UnauthorizedObjectResult>(result);
        MockSignInManager.Verify(m => m.PasswordSignInAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task RefreshToken_SoftDeletedUser_ReturnsUnauthorized()
    {
        var user = MakeUser();
        user.IsDeleted = true;
        var db = CreateDbContext();
        var jwt = GenerateTestJwt(user.Id);
        MockUserManager.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);

        var result = await CreateAuthController(db).RefreshToken(
            new RefreshTokenRequestDto { Token = jwt, RefreshToken = "anything" });

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task RefreshToken_RevokedTokenReplay_RevokesEntireFamily()
    {
        var user = MakeUser();
        var db = CreateDbContext();
        var jwt = GenerateTestJwt(user.Id, expired: true);

        var stolenRaw = "stolen-refresh-token";
        var stolenHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(stolenRaw)));

        // Stolen token already revoked (legitimate user already rotated past it).
        db.RefreshTokens.Add(new RefreshToken
        {
            Token = stolenHash, UserId = user.Id,
            Revoked = DateTime.UtcNow.AddMinutes(-5),
            Expires = DateTime.UtcNow.AddMonths(1),
            Created = DateTime.UtcNow.AddMinutes(-10)
        });
        // Other live tokens in the same family that should be revoked on replay.
        db.RefreshTokens.Add(new RefreshToken
        {
            Token = "other-live-1", UserId = user.Id,
            Expires = DateTime.UtcNow.AddMonths(1),
            Created = DateTime.UtcNow
        });
        db.RefreshTokens.Add(new RefreshToken
        {
            Token = "other-live-2", UserId = user.Id,
            Expires = DateTime.UtcNow.AddMonths(1),
            Created = DateTime.UtcNow
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        MockUserManager.Setup(m => m.FindByIdAsync(user.Id)).ReturnsAsync(user);
        MockUserManager.Setup(m => m.GetRolesAsync(user)).ReturnsAsync(new List<string>());

        var result = await CreateAuthController(db).RefreshToken(
            new RefreshTokenRequestDto { Token = jwt, RefreshToken = stolenRaw });

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(0, db.RefreshTokens.Count(t => t.UserId == user.Id && t.Revoked == null));
    }

    [Fact]
    public async Task VerifyEmail_UnknownEmail_ReturnsGenericBadRequest()
    {
        MockUserManager.Setup(m => m.FindByEmailAsync(It.IsAny<string>())).ReturnsAsync((User?)null);

        var result = await CreateAuthController().VerifyEmail(
            new VerifyEmailDto { Email = "ghost@test.com", Code = "ABCDEF" });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ResetPassword_UnknownEmail_ReturnsGenericBadRequest()
    {
        MockUserManager.Setup(m => m.FindByEmailAsync(It.IsAny<string>())).ReturnsAsync((User?)null);

        var result = await CreateAuthController().ResetPassword(
            new ResetPasswordDto { Email = "ghost@test.com", Code = "ABCDEF", NewPassword = "Password1!" });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    private static string Sha256B64(string t)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(t)));
    }

    [Fact]
    public async Task ResetPassword_Success_ClearsLockout()
    {
        // A user who locked themselves out with the old password must be able to sign in with the new
        // one immediately — the reset clears the lockout.
        var user = new User
        {
            Id = "u1", UserName = "user", Email = "user@test.com", FirstName = "F", LastName = "L",
            PasswordResetCodeHash = Sha256B64("ABCDEF"),
            PasswordResetCodeExpiration = DateTime.UtcNow.AddMinutes(10),
            PasswordResetAttempts = 0,
            LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(20),
            AccessFailedCount = 3,
        };
        MockUserManager.Setup(m => m.FindByEmailAsync(user.Email!)).ReturnsAsync(user);
        MockUserManager.Setup(m => m.GeneratePasswordResetTokenAsync(user)).ReturnsAsync("token");
        MockUserManager.Setup(m => m.ResetPasswordAsync(user, "token", It.IsAny<string>())).ReturnsAsync(IdentityResult.Success);
        MockUserManager.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);

        var result = await CreateAuthController().ResetPassword(
            new ResetPasswordDto { Email = user.Email!, Code = "ABCDEF", NewPassword = "Password1!" });

        Assert.IsType<OkObjectResult>(result);
        Assert.Null(user.LockoutEnd);
        Assert.Equal(0, user.AccessFailedCount);
        Assert.Null(user.PasswordResetCodeHash);
        Assert.Equal(0, user.PasswordResetAttempts);
    }

    [Fact]
    public async Task Register_PersistsTermsFields()
    {
        MockUserManager.Setup(m => m.FindByEmailAsync(It.IsAny<string>())).ReturnsAsync((User?)null);
        MockUserManager.Setup(m => m.CreateAsync(It.IsAny<User>(), It.IsAny<string>()))
            .ReturnsAsync(IdentityResult.Success);
        MockUserManager.Setup(m => m.AddToRoleAsync(It.IsAny<User>(), It.IsAny<string>()))
            .ReturnsAsync(IdentityResult.Success);
        MockUserManager.Setup(m => m.UpdateAsync(It.IsAny<User>())).ReturnsAsync(IdentityResult.Success);

        User? captured = null;
        MockUserManager.Setup(m => m.CreateAsync(It.IsAny<User>(), It.IsAny<string>()))
            .Callback<User, string>((u, _) => captured = u)
            .ReturnsAsync(IdentityResult.Success);

        MockMapper.Setup(m => m.Map<User>(It.IsAny<RegisterUserDto>()))
            .Returns((RegisterUserDto src) => new User
            {
                Id = "new-user", UserName = src.UserName, Email = src.Email,
                FirstName = src.FirstName, LastName = src.LastName
            });

        var dto = MakeRegisterDto();
        dto.TermsVersion = "v1-2026-05";
        await CreateAuthController(CreateDbContext()).Register(dto);

        Assert.NotNull(captured);
        Assert.NotNull(captured!.TermsAcceptedAt);
        Assert.Equal("v1-2026-05", captured.TermsVersion);
        // RemoteIpAddress is null in the test ControllerContext, so the truncator returns "" -- still set, just empty.
        Assert.NotNull(captured.TermsAcceptedIp);
    }

    [Fact]
    public async Task Register_EmailAlreadyExists_ReturnsGenericOkToPreventEnumeration()
    {
        MockUserManager.Setup(m => m.FindByEmailAsync("exists@test.com")).ReturnsAsync(MakeUser());

        var result = await CreateAuthController().Register(MakeRegisterDto(email: "exists@test.com"));

        Assert.IsType<OkObjectResult>(result);
        MockUserManager.Verify(m => m.CreateAsync(It.IsAny<User>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Register_DuplicateUserName_ReturnsFieldKeyedError()
    {
        MockUserManager.Setup(m => m.FindByEmailAsync(It.IsAny<string>())).ReturnsAsync((User?)null);
        MockMapper.Setup(m => m.Map<User>(It.IsAny<RegisterUserDto>()))
            .Returns((RegisterUserDto src) => new User
            {
                Id = "u", UserName = src.UserName, Email = src.Email,
                FirstName = src.FirstName, LastName = src.LastName
            });
        MockUserManager.Setup(m => m.CreateAsync(It.IsAny<User>(), It.IsAny<string>()))
            .ReturnsAsync(IdentityResult.Failed(new IdentityError
            {
                Code = "DuplicateUserName",
                Description = "Username 'user' is already taken."
            }));

        var result = await CreateAuthController(CreateDbContext()).Register(MakeRegisterDto());

        // Field-keyed shape the client can map to the username field (issue #75),
        // not the raw Identity array that surfaced as a generic "failed to register".
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var errorsObj = bad.Value!.GetType().GetProperty("errors")!.GetValue(bad.Value);
        var errors = Assert.IsAssignableFrom<IDictionary<string, string[]>>(errorsObj);
        Assert.True(errors.ContainsKey("UserName"));
        Assert.Contains("already taken", errors["UserName"][0]);
    }
}
