using SjolystInnovation.AppKit.Email;

/// <summary>Sends through SjolystInnovation.AppKit.Email, which logs a refused email.</summary>
public class EmailService(IEmailSender sender) : IEmailService
{
    public async Task<bool> SendEmailAsync(string email, string subject, string message, IReadOnlyList<EmailAttachment>? attachments = null)
    {
        var result = await sender.SendAsync(new SjolystInnovation.AppKit.Email.EmailMessage(email, subject, message)
        {
            Attachments = (attachments ?? []).Select(a => new SjolystInnovation.AppKit.Email.EmailAttachment(a.FileName, a.Content)).ToList(),
        });
        return result.Sent;
    }
}
