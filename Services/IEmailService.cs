using garge_api.Dtos.Admin;

public interface IEmailService
{
    Task SendEmailAsync(string email, string subject, string message);
    Task<EmailStatsDto> GetEmailStatsAsync(int days = 30);
}
