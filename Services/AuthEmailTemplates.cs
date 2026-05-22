using garge_api.Models.Admin;
using System.Web;

namespace garge_api.Services
{
    /// <summary>
    /// Builds the branded HTML for auth code emails (email verification, password
    /// reset) on the shared <see cref="EmailLayout"/>, so they match the order /
    /// invoice / subscription mails instead of being plain text.
    /// </summary>
    public static class AuthEmailTemplates
    {
        public static string VerificationCode(AppSettings s, string? firstName, string code) =>
            Build(s,
                subtitle: "EMAIL VERIFICATION",
                heading: "Confirm your email",
                intro: $"welcome to {H(s.CompanyName)}. Enter the code below to verify your email address and activate your account.",
                firstName: firstName,
                code: code,
                expiry: "1 hour",
                ignoreNote: $"If you didn't create a {H(s.CompanyName)} account, you can ignore this email.");

        public static string PasswordReset(AppSettings s, string? firstName, string code) =>
            Build(s,
                subtitle: "PASSWORD RESET",
                heading: "Reset your password",
                intro: $"we received a request to reset your {H(s.CompanyName)} password. Enter the code below to continue.",
                firstName: firstName,
                code: code,
                expiry: "30 minutes",
                ignoreNote: "If you didn't request a password reset, you can ignore this email — your password won't change.");

        private static string H(string? v) => HttpUtility.HtmlEncode(v ?? string.Empty);

        private static string Build(
            AppSettings s, string subtitle, string heading, string intro,
            string? firstName, string code, string expiry, string ignoreNote)
        {
            var greeting = string.IsNullOrWhiteSpace(firstName) ? "Hi there," : $"Hi {H(firstName)},";

            var body = $$"""
                <h1>{{H(heading)}}</h1>
                <p>{{greeting}} {{intro}}</p>
                <div class="code-box">{{H(code)}}</div>
                <div class="footer">
                  <p>This code expires in {{expiry}}. {{ignoreNote}}</p>
                </div>
                """;

            return EmailLayout.Render(s, new EmailLayout.Meta { Subtitle = subtitle }, body);
        }
    }
}
