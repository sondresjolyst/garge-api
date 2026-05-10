using System.Threading.Tasks;
using brevo_csharp.Api;
using brevo_csharp.Client;
using brevo_csharp.Model;
using garge_api.Dtos.Admin;

public class EmailService : IEmailService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async System.Threading.Tasks.Task SendEmailAsync(
        string email, string subject, string message,
        IReadOnlyList<EmailAttachment>? attachments = null)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            _logger.LogError("Attempted to send email with a null or empty recipient address.");
            throw new ArgumentException("Recipient email address cannot be null or empty.", nameof(email));
        }

        var brevoSettings = _configuration.GetSection("BrevoSettings").Get<BrevoSettings>();
        brevo_csharp.Client.Configuration.Default.ApiKey["api-key"] = brevoSettings!.ApiKey;

        var apiInstance = new TransactionalEmailsApi();
        var sendSmtpEmail = new SendSmtpEmail
        {
            HtmlContent = message,
            Subject = subject,
            Sender = new SendSmtpEmailSender
            {
                Email = brevoSettings.SenderEmail,
                Name = brevoSettings.SenderName
            },
            To = new List<SendSmtpEmailTo>
            {
                new SendSmtpEmailTo(email)
            }
        };

        if (attachments != null && attachments.Count > 0)
        {
            sendSmtpEmail.Attachment = attachments
                .Select(a => new SendSmtpEmailAttachment(content: a.Content, name: a.FileName))
                .ToList();
        }

        try
        {
            var result = await apiInstance.SendTransacEmailAsync(sendSmtpEmail);
        }
        catch (ApiException ex)
        {
            _logger.LogError("Failed to send email. Error: {Error}", ex.Message);
        }
    }

    public async Task<EmailStatsDto> GetEmailStatsAsync(int days = 30)
    {
        var brevoSettings = _configuration.GetSection("BrevoSettings").Get<BrevoSettings>();
        brevo_csharp.Client.Configuration.Default.ApiKey["api-key"] = brevoSettings!.ApiKey;

        var apiInstance = new TransactionalEmailsApi();
        var report = await apiInstance.GetSmtpReportAsync(limit: days, days: days);

        var stats = new EmailStatsDto { Days = days };
        if (report?.Reports != null)
        {
            foreach (var r in report.Reports)
            {
                stats.Requests += r.Requests ?? 0;
                stats.Delivered += r.Delivered ?? 0;
                stats.HardBounces += r.HardBounces ?? 0;
                stats.SoftBounces += r.SoftBounces ?? 0;
                stats.SpamReports += r.SpamReports ?? 0;
                stats.Blocked += r.Blocked ?? 0;
                stats.Invalid += r.Invalid ?? 0;
            }
        }
        return stats;
    }
}

public class BrevoSettings
{
    public required string ApiKey { get; set; }
    public required string SenderEmail { get; set; }
    public required string SenderName { get; set; }
}
