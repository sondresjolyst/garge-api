using garge_api.Models.Switch;
using Newtonsoft.Json;
using System.Net;
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
        if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Rejected webhook URL (invalid or non-HTTPS): {WebhookUrl}", webhookUrl);
            return;
        }

        if (await IsPrivateHostAsync(uri.Host))
        {
            _logger.LogWarning("Rejected webhook URL targeting private/internal host: {Host}", uri.Host);
            return;
        }

        var payload = JsonConvert.SerializeObject(switchData);
        var content = new StringContent(payload, Encoding.UTF8, "application/json");

        try
        {
            var response = await _httpClient.PostAsync(webhookUrl, content);
            response.EnsureSuccessStatusCode();
            _logger.LogInformation("Webhook notified successfully: {WebhookUrl}", webhookUrl);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("HTTP request failed for webhook {WebhookUrl}: {Message}", webhookUrl, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to notify webhook {WebhookUrl}: {Message}", webhookUrl, ex.Message);
        }
    }

    private static async Task<bool> IsPrivateHostAsync(string host)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host);
            return addresses.Any(IsPrivateAddress);
        }
        catch
        {
            return true;
        }
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;

        var bytes = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            System.Net.Sockets.AddressFamily.InterNetwork => bytes[0] switch
            {
                0 => true,
                10 => true,
                127 => true,
                169 when bytes[1] == 254 => true,
                172 when bytes[1] >= 16 && bytes[1] <= 31 => true,
                192 when bytes[1] == 168 => true,
                _ => false
            },
            System.Net.Sockets.AddressFamily.InterNetworkV6 =>
                address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.Equals(IPAddress.IPv6Loopback),
            _ => true
        };
    }
}
