public sealed class EmailAttachment
{
    public required string FileName { get; set; }
    public required byte[] Content { get; set; }
    public string ContentType { get; set; } = "application/octet-stream";
}

public interface IEmailService
{
    /// <summary>Sends an email. Returns false when Brevo refused it, which is logged.</summary>
    Task<bool> SendEmailAsync(string email, string subject, string message, IReadOnlyList<EmailAttachment>? attachments = null);
}
