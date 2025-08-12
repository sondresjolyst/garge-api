using System.Threading.Tasks;
using brevo_csharp.Api;
using brevo_csharp.Client;
using brevo_csharp.Model;

public class EmailService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async System.Threading.Tasks.Task SendEmailAsync(string email, string subject, string message)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            _logger.LogError("Attempted to send email with a null or empty recipient address.");
            throw new ArgumentException("Recipient email address cannot be null or empty.", nameof(email));
        }

        var brevoSettings = _configuration.GetSection("BrevoSettings").Get<BrevoSettings>();
        brevo_csharp.Client.Configuration.Default.ApiKey.Add("api-key", brevoSettings!.ApiKey);

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

        try
        {
            var result = await apiInstance.SendTransacEmailAsync(sendSmtpEmail);
            _logger.LogInformation("Email sent to {Email} with Brevo message ID {MessageId}", email, result.MessageId);
        }
        catch (ApiException ex)
        {
            _logger.LogError("Failed to send email to {Email}. Error: {Error}", email, ex.Message);
        }
    }
}

public class BrevoSettings
{
    public required string ApiKey { get; set; }
    public required string SenderEmail { get; set; }
    public required string SenderName { get; set; }
}
