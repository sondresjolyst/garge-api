using garge_api.Dtos.Admin;

public sealed class EmailAttachment
{
    public required string FileName { get; set; }
    public required byte[] Content { get; set; }
    public string ContentType { get; set; } = "application/octet-stream";
}

public interface IEmailService
{
    Task SendEmailAsync(string email, string subject, string message, IReadOnlyList<EmailAttachment>? attachments = null);
    Task<EmailStatsDto> GetEmailStatsAsync(int days = 30);
}
