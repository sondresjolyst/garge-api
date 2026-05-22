using garge_api.Models.Admin;
using garge_api.Services;
using Xunit;

namespace garge_api.Tests;

public class AuthEmailTemplatesTests
{
    private static AppSettings Settings() => new()
    {
        CompanyName = "Test Co",
        CompanyLegalName = "Test Co AS",
        CompanyOrgNumber = "000 000 000",
        CompanyAddress = "1 Example Street, 0000 Testby",
        CompanyEmail = "test@example.com",
    };

    [Fact]
    public void VerificationCode_RendersBrandedHtml_WithCodeAndCompany()
    {
        var html = AuthEmailTemplates.VerificationCode(Settings(), "Sondre", "ABC123");

        Assert.Contains("<!DOCTYPE html>", html);     // shared layout shell, not plain text
        Assert.Contains("Test Co", html);             // brand header band (from settings)
        Assert.Contains("Confirm your email", html);  // heading
        Assert.Contains("VERIFY", html);              // bold meta word in header
        Assert.Contains("EMAIL VERIFICATION", html);  // meta subtitle
        Assert.Contains("Verification code", html);   // blue accent code label
        Assert.Contains("ABC123", html);              // the code
        Assert.Contains("Hi Sondre,", html);          // personalised greeting
        Assert.Contains("1 hour", html);              // verification expiry
    }

    [Fact]
    public void PasswordReset_RendersBrandedHtml_WithCodeAndExpiry()
    {
        var html = AuthEmailTemplates.PasswordReset(Settings(), "Sondre", "ZZ999");

        Assert.Contains("<!DOCTYPE html>", html);
        Assert.Contains("Reset your password", html);
        Assert.Contains("PASSWORD RESET", html);
        Assert.Contains("ZZ999", html);
        Assert.Contains("30 minutes", html);          // reset code expiry (differs from verification)
    }

    [Fact]
    public void VerificationCode_MissingFirstName_FallsBackToGenericGreeting()
    {
        var html = AuthEmailTemplates.VerificationCode(Settings(), null, "ABC123");

        Assert.Contains("Hi there,", html);
        Assert.DoesNotContain("Hi ,", html);
    }

    [Fact]
    public void VerificationCode_HtmlEncodesFirstName()
    {
        var html = AuthEmailTemplates.VerificationCode(Settings(), "<b>x</b>", "ABC123");

        Assert.DoesNotContain("<b>x</b>", html);
        Assert.Contains("&lt;b&gt;x&lt;/b&gt;", html);
    }
}
