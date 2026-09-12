using garge_api.Models.Admin;
using System.Web;

namespace garge_api.Services
{
    public static class SecurityEmailTemplates
    {
        public static string Alert(AppSettings s, string? firstName, string heading, string message)
        {
            var greeting = string.IsNullOrWhiteSpace(firstName) ? "Hi there," : $"Hi {H(firstName)},";

            var body = $$"""
                <h1>{{H(heading)}}</h1>
                <p>{{greeting}}</p>
                <p>{{H(message)}}</p>
                """;

            return EmailLayout.Render(s, new EmailLayout.Meta
            {
                Number = "ALERT",
                Subtitle = $"GARGE SECURITY  ·  {LocalTime.Format(DateTime.UtcNow)}"
            }, body);
        }

        private static string H(string? v) => HttpUtility.HtmlEncode(v ?? string.Empty);
    }
}
