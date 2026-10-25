using garge_api.Dtos.Auth;
using garge_api.Models.Auth;
using SjolystInnovation.AppKit.Auth;
using SjolystInnovation.AppKit.SiteSettings;
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
    public async Task VerifyEmail_UnknownEmail_ReturnsGenericBadRequest()
    {
        MockUserManager.Setup(m => m.FindByEmailAsync(It.IsAny<string>())).ReturnsAsync((User?)null);

        var result = await CreateAuthController().VerifyEmail(
            new VerifyEmailDto { Email = "ghost@test.com", Code = "ABCDEF" });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    private User UnverifiedUser(string code)
    {
        var user = MakeUser();
        user.EmailConfirmed = false;
        user.EmailVerificationCodeHash = Sha256B64(code);
        user.EmailVerificationCodeExpiration = DateTime.UtcNow.AddMinutes(30);
        MockUserManager.Setup(m => m.FindByEmailAsync(user.Email!)).ReturnsAsync(user);
        MockUserManager.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);
        return user;
    }

    [Fact]
    public async Task VerifyEmail_RightCode_ConfirmsTheEmail()
    {
        var user = UnverifiedUser("ABC123");

        var result = await CreateAuthController().VerifyEmail(new VerifyEmailDto { Email = user.Email!, Code = "ABC123" });

        Assert.IsType<OkObjectResult>(result);
        Assert.True(user.EmailConfirmed);
        Assert.Null(user.EmailVerificationCodeHash);
        MockUserManager.Verify(m => m.UpdateAsync(user), Times.Once);
    }

    [Fact]
    public async Task VerifyEmail_WrongCode_CountsTheAttempt()
    {
        var user = UnverifiedUser("ABC123");

        var result = await CreateAuthController().VerifyEmail(new VerifyEmailDto { Email = user.Email!, Code = "WRONG1" });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(user.EmailConfirmed);
        Assert.Equal(1, user.EmailVerificationAttempts);
        MockUserManager.Verify(m => m.UpdateAsync(user), Times.Once);
    }

    [Fact]
    public async Task VerifyEmail_AfterFiveWrongCodes_RefusesEvenTheRightOne()
    {
        var user = UnverifiedUser("ABC123");
        var controller = CreateAuthController();
        for (var i = 0; i < 5; i++)
            await controller.VerifyEmail(new VerifyEmailDto { Email = user.Email!, Code = "WRONG1" });

        var result = await controller.VerifyEmail(new VerifyEmailDto { Email = user.Email!, Code = "ABC123" });

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(429, status.StatusCode);
        Assert.False(user.EmailConfirmed);
    }

    [Fact]
    public async Task VerifyEmail_AlreadyVerified_ReturnsGenericBadRequest()
    {
        var user = UnverifiedUser("ABC123");
        user.EmailConfirmed = true;

        var result = await CreateAuthController().VerifyEmail(new VerifyEmailDto { Email = user.Email!, Code = "ABC123" });

        Assert.IsType<BadRequestObjectResult>(result);
        MockUserManager.Verify(m => m.UpdateAsync(It.IsAny<User>()), Times.Never);
    }

    [Fact]
    public async Task ResendEmailConfirmation_StartsTheAttemptsOver()
    {
        var user = UnverifiedUser("ABC123");
        user.EmailVerificationAttempts = 5;

        await CreateAuthController().ResendEmailConfirmation(new ResendEmailConfirmationDto { Email = user.Email! });

        Assert.Equal(0, user.EmailVerificationAttempts);
        Assert.NotEqual(Sha256B64("ABC123"), user.EmailVerificationCodeHash);
        Assert.True(user.EmailVerificationCodeExpiration > DateTime.UtcNow.AddMinutes(59));
        MockVerificationEmails.Verify(m => m.SendEmailVerificationAsync(user, It.Is<string>(c => c.Length == 6), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static string Sha256B64(string t)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(t)));
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
        MockVerificationEmails.Verify(m => m.SendEmailVerificationAsync(captured, It.Is<string>(c => c.Length == 6), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("::ffff:203.0.113.7", "203.0.113.0")]
    [InlineData("203.0.113.7", "203.0.113.0")]
    [InlineData("2001:db8:abcd:1234::1", "2001:db8:abcd::")]
    public async Task Register_StoresTheTruncatedClientAddress(string remote, string expected)
    {
        MockUserManager.Setup(m => m.FindByEmailAsync(It.IsAny<string>())).ReturnsAsync((User?)null);
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

        var controller = CreateAuthController(CreateDbContext());
        controller.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };
        controller.HttpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(remote);

        await controller.Register(MakeRegisterDto(), TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal(expected, captured!.TermsAcceptedIp);
    }

    // The version the legal page answers for a page saved at this time.
    private const string TermsVersion = "2026-10-19T12:34:56.123456Z";

    private garge_api.Models.ApplicationDbContext DbWithTerms(string locale = "en")
    {
        var db = CreateDbContext();
        db.LegalPages.Add(new LegalPage
        {
            Key = "terms", Locale = locale, Title = "Terms of Service", BodyMarkdown = "Terms.",
            UpdatedAt = new DateTime(2026, 10, 19, 12, 34, 56, DateTimeKind.Utc).AddTicks(1_234_560)
        });
        db.LegalPages.Add(new LegalPage { Key = "privacy", Locale = locale, Title = "Privacy Policy", UpdatedAt = DateTime.UtcNow });
        db.SaveChanges();
        return db;
    }

    private void SetUpSuccessfulRegistration(Action<User>? created = null)
    {
        MockUserManager.Setup(m => m.FindByEmailAsync(It.IsAny<string>())).ReturnsAsync((User?)null);
        MockUserManager.Setup(m => m.AddToRoleAsync(It.IsAny<User>(), It.IsAny<string>())).ReturnsAsync(IdentityResult.Success);
        MockUserManager.Setup(m => m.UpdateAsync(It.IsAny<User>())).ReturnsAsync(IdentityResult.Success);
        MockUserManager.Setup(m => m.CreateAsync(It.IsAny<User>(), It.IsAny<string>()))
            .Callback<User, string>((u, _) => created?.Invoke(u))
            .ReturnsAsync(IdentityResult.Success);
        MockMapper.Setup(m => m.Map<User>(It.IsAny<RegisterUserDto>()))
            .Returns((RegisterUserDto src) => new User
            {
                Id = "new-user", UserName = src.UserName, Email = src.Email,
                FirstName = src.FirstName, LastName = src.LastName
            });
    }

    [Fact]
    public async Task Register_WithTheCurrentTermsVersion_StoresIt()
    {
        User? captured = null;
        SetUpSuccessfulRegistration(u => captured = u);
        var dto = MakeRegisterDto();
        dto.TermsVersion = TermsVersion;

        var result = await CreateAuthController(DbWithTerms()).Register(dto, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(TermsVersion, captured!.TermsVersion);
    }

    [Theory]
    [InlineData("2026-10-19T12:34:56.123455Z")]
    [InlineData("v1-test")]
    [InlineData("")]
    public async Task Register_WithOtherTermsThanTheCurrent_Answers409TermsChanged(string version)
    {
        SetUpSuccessfulRegistration();
        var dto = MakeRegisterDto();
        dto.TermsVersion = version;

        var result = await CreateAuthController(DbWithTerms()).Register(dto, TestContext.Current.CancellationToken);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal("termsChanged", conflict.Value!.GetType().GetProperty("code")!.GetValue(conflict.Value));
        Assert.Equal("The terms have changed. Read the new terms and accept them again.", conflict.Value.GetType().GetProperty("message")!.GetValue(conflict.Value));
        MockUserManager.Verify(m => m.CreateAsync(It.IsAny<User>(), It.IsAny<string>()), Times.Never);
        MockVerificationEmails.Verify(m => m.SendEmailVerificationAsync(It.IsAny<User>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Register_WithTermsOnlyInAnotherLanguage_ChecksTheFallback()
    {
        // Garge's pages are English. Terms saved under another language are not garge's terms.
        SetUpSuccessfulRegistration();
        var dto = MakeRegisterDto();
        dto.TermsVersion = "v1-test";

        var result = await CreateAuthController(DbWithTerms(locale: "no")).Register(dto, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Register_WithNoTermsPage_AcceptsTheVersionSent()
    {
        User? captured = null;
        SetUpSuccessfulRegistration(u => captured = u);
        var dto = MakeRegisterDto();
        dto.TermsVersion = "v1-test";

        var result = await CreateAuthController(CreateDbContext()).Register(dto, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("v1-test", captured!.TermsVersion);
    }

    [Theory]
    [InlineData(27, true)]
    [InlineData(40, true)]
    [InlineData(41, false)]
    public void RegisterUserDto_TermsVersionHoldsUpTo40Characters(int length, bool valid)
    {
        var dto = MakeRegisterDto();
        dto.Password = "password1";
        dto.TermsVersion = new string('x', length);

        Assert.Equal(valid, System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
            dto, new System.ComponentModel.DataAnnotations.ValidationContext(dto), null, validateAllProperties: true));
    }

    [Fact]
    public void User_TermsVersionColumnHolds40Characters()
    {
        using var db = CreateDbContext();

        Assert.Equal(40, db.Model.FindEntityType(typeof(User))!.FindProperty(nameof(User.TermsVersion))!.GetMaxLength());
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
