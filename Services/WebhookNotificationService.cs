using garge_api.Models;
using Newtonsoft.Json;
using System.Text;

public class WebhookNotificationService
{
    private readonly HttpClient _httpClient;

    public WebhookNotificationService(HttpClient httpClient)
    {
        Console.WriteLine("WebhookNotificationService initialized");
        _httpClient = httpClient;
    }

    public async Task NotifyClientAsync(string webhookUrl, SwitchData switchData)
    {
        if (!Uri.IsWellFormedUriString(webhookUrl, UriKind.Absolute))
        {
            Console.WriteLine($"Invalid webhook URL: {webhookUrl}");
            return;
        }

        var payload = JsonConvert.SerializeObject(switchData);
        var content = new StringContent(payload, Encoding.UTF8, "application/json");

        try
        {
            Console.WriteLine($"Sending payload to webhook: {payload}");
            var response = await _httpClient.PostAsync(webhookUrl, content);
            response.EnsureSuccessStatusCode();
            Console.WriteLine($"Successfully notified webhook: {webhookUrl}");
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"HTTP request failed for webhook: {webhookUrl}. Error: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to notify webhook: {webhookUrl}. Error: {ex.Message}");
        }
    }
}
