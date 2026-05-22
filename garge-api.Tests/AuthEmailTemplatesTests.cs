using garge_api.Models.Admin;
using garge_api.Services;
using Xunit;

namespace garge_api.Tests;

public class AuthEmailTemplatesTests
{
    private static AppSettings Settings() => new()
    {
        CompanyName = "Garge",
        CompanyLegalName = "Sjølyst Innovations",
        CompanyOrgNumber = "934 531 035",
        CompanyAddress = "Mårvegen 21a, 4347 Lye",
        CompanyEmail = "post@garge.example",
    };

    [Fact]
    public void VerificationCode_RendersBrandedHtml_WithCodeAndCompany()
    {
        var html = AuthEmailTemplates.VerificationCode(Settings(), "Sondre", "ABC123");

        Assert.Contains("<!DOCTYPE html>", html);     // shared layout shell, not plain text
        Assert.Contains("Garge", html);               // brand header band
        Assert.Contains("Confirm your email", html);  // heading
        Assert.Contains("EMAIL VERIFICATION", html);  // meta subtitle
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
