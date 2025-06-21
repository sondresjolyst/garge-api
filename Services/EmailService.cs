using SendGrid;
using SendGrid.Helpers.Mail;

public class EmailService
{
    private readonly IConfiguration _configuration;

    public EmailService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task SendEmailAsync(string email, string subject, string message)
    {
        var sendGridSettings = _configuration.GetSection("SendGridSettings").Get<SendGridSettings>();
        var client = new SendGridClient(sendGridSettings!.ApiKey);
        var from = new EmailAddress(sendGridSettings.SenderEmail, sendGridSettings.SenderName);
        var to = new EmailAddress(email);
        var msg = MailHelper.CreateSingleEmail(from, to, subject, message, message);

        // Disable click tracking
        msg.SetClickTracking(false, false);

        await client.SendEmailAsync(msg);
    }
}

public class SendGridSettings
{
    public required string ApiKey { get; set; }
    public required string SenderEmail { get; set; }
    public required string SenderName { get; set; }
}
