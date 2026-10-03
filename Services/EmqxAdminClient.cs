using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace garge_api.Services
{
    /// <summary>
    /// The broker's own management API, used to disconnect a client. Removing an ACL row does not
    /// end a subscription the client already holds. The broker authorises a subscription when
    /// it is made, so a gateway that loses a lease keeps receiving that device's commands until its
    /// session ends. Disconnecting it forces the subscription to be re-authorised on reconnect.
    /// </summary>
    public interface IEmqxAdminClient
    {
        /// <summary>
        /// Disconnects one client id. Returns false when the broker reports it is not connected,
        /// or when no management credentials are configured.
        /// </summary>
        Task<bool> KickClientAsync(string clientId, CancellationToken cancellationToken = default);
    }

    public class EmqxAdminClient : IEmqxAdminClient
    {
        private readonly HttpClient _http;
        private readonly ILogger<EmqxAdminClient> _logger;
        private readonly string? _baseUrl;

        public EmqxAdminClient(HttpClient http, IConfiguration configuration, ILogger<EmqxAdminClient> logger)
        {
            _http = http;
            _logger = logger;

            _baseUrl = configuration["Emqx:ApiUrl"]?.TrimEnd('/');
            var apiKey = configuration["Emqx:ApiKey"];
            var apiSecret = configuration["Emqx:ApiSecret"];

            if (!string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(apiSecret))
            {
                var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{apiKey}:{apiSecret}"));
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
            }
        }

        private bool Configured =>
            !string.IsNullOrWhiteSpace(_baseUrl) && _http.DefaultRequestHeaders.Authorization != null;

        public async Task<bool> KickClientAsync(string clientId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(clientId))
            {
                return false;
            }

            // Deployments without management credentials still run; the lease and the ACL rows
            // move, and the stale subscription clears whenever the gateway next reconnects.
            if (!Configured)
            {
                _logger.LogWarning("Skipped EMQX client kick: no management credentials configured {@LogData}",
                    new { ClientId = clientId });
                return false;
            }

            try
            {
                using var response = await _http.DeleteAsync(
                    $"{_baseUrl}/api/v5/clients/{Uri.EscapeDataString(clientId)}", cancellationToken);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    // Not connected, so there is no session holding the old subscription.
                    return false;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("EMQX client kick failed {@LogData}",
                        new { ClientId = clientId, StatusCode = (int)response.StatusCode });
                    return false;
                }

                _logger.LogInformation("EMQX client kicked {@LogData}", new { ClientId = clientId });
                return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning(ex, "EMQX client kick could not reach the broker {@LogData}",
                    new { ClientId = clientId });
                return false;
            }
        }
    }
}
