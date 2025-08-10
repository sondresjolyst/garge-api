using garge_api.Models.Switch;
using Newtonsoft.Json;
using System.Text;

public class WebhookNotificationService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WebhookNotificationService> _logger;

    public WebhookNotificationService(HttpClient httpClient, ILogger<WebhookNotificationService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        _logger.LogInformation("WebhookNotificationService initialized");
    }

    public async Task NotifyClientAsync(string webhookUrl, SwitchData switchData)
    {
        if (!Uri.IsWellFormedUriString(webhookUrl, UriKind.Absolute))
        {
            _logger.LogWarning($"Invalid webhook URL: {webhookUrl}");
            return;
        }

        var payload = JsonConvert.SerializeObject(switchData);
        var content = new StringContent(payload, Encoding.UTF8, "application/json");

        try
        {
            _logger.LogWarning($"Sending payload to webhook: {payload}");
            var response = await _httpClient.PostAsync(webhookUrl, content);
            response.EnsureSuccessStatusCode();
            _logger.LogWarning($"Successfully notified webhook: {webhookUrl}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning($"HTTP request failed for webhook: {webhookUrl}. Error: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to notify webhook: {webhookUrl}. Error: {ex.Message}");
        }
    }
}
