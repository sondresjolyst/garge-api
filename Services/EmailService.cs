using garge_api.Dtos.Admin;
using SjolystInnovation.AppKit.Email;

/// <summary>Sends through SjolystInnovation.AppKit.Email, which logs a refused email.</summary>
public class EmailService(IEmailSender sender, IEmailStats stats) : IEmailService
{
    public async Task<bool> SendEmailAsync(string email, string subject, string message, IReadOnlyList<EmailAttachment>? attachments = null)
    {
        var result = await sender.SendAsync(new SjolystInnovation.AppKit.Email.EmailMessage(email, subject, message)
        {
            Attachments = (attachments ?? []).Select(a => new SjolystInnovation.AppKit.Email.EmailAttachment(a.FileName, a.Content)).ToList(),
        });
        return result.Sent;
    }

    public async Task<EmailStatsDto> GetEmailStatsAsync(int days = 30)
    {
        var s = await stats.GetAsync(days);
        return new EmailStatsDto
        {
            Days = s.Days,
            Requests = s.Requests,
            Delivered = s.Delivered,
            HardBounces = s.HardBounces,
            SoftBounces = s.SoftBounces,
            SpamReports = s.SpamReports,
            Blocked = s.Blocked,
            Invalid = s.Invalid,
        };
    }
}
