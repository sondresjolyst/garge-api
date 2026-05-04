using garge_api.Models.Shop;
using garge_api.Models.Subscription;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace garge_api.Services
{
    public class VippsService : IVippsService
    {
        private readonly HttpClient _http;
        private readonly VippsOptions _opts;
        private readonly AppOptions _appOpts;
        private readonly IMemoryCache _cache;
        private readonly ILogger<VippsService> _logger;
        private readonly string _systemName;
        private readonly string _systemVersion;

        private const string TokenCacheKey = "vipps_access_token";
        private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

        public VippsService(
            HttpClient http,
            IOptions<VippsOptions> opts,
            IOptions<AppOptions> appOpts,
            IMemoryCache cache,
            ILogger<VippsService> logger)
        {
            _http = http;
            _opts = opts.Value;
            _appOpts = appOpts.Value;
            _cache = cache;
            _logger = logger;

            var assembly = Assembly.GetExecutingAssembly();
            _systemName = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "garge";
            _systemVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                             ?? assembly.GetName().Version?.ToString()
                             ?? "1.0.0";
        }

        public async Task<string> GetAccessTokenAsync()
        {
            if (_cache.TryGetValue(TokenCacheKey, out string? cached) && cached != null)
                return cached;

            var request = new HttpRequestMessage(HttpMethod.Post, $"{_opts.BaseUrl}/accesstoken/get");
            request.Headers.Add("client_id", _opts.ClientId);
            request.Headers.Add("client_secret", _opts.ClientSecret);
            request.Headers.Add("Ocp-Apim-Subscription-Key", _opts.SubscriptionKey);
            request.Content = new StringContent(string.Empty, Encoding.UTF8, "application/json");

            var response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var token = doc.RootElement.GetProperty("access_token").GetString()!;
            // expires_in is returned as a string by Vipps, not an int
            var expiresInStr = doc.RootElement.GetProperty("expires_in").GetString() ?? "3600";
            var expiresIn = int.Parse(expiresInStr);

            _cache.Set(TokenCacheKey, token, TimeSpan.FromSeconds(expiresIn - 60));
            _logger.LogInformation("Vipps access token refreshed, expires in {ExpiresIn}s", expiresIn);
            return token;
        }

        public async Task<VippsCreateAgreementResponse> CreateAgreementAsync(
            Product product, string userId, string redirectUrl, string phoneNumber, int effectivePriceInOre)
        {
            var token = await GetAccessTokenAsync();

            var body = new
            {
                pricing = new
                {
                    type = "LEGACY",
                    amount = effectivePriceInOre,
                    currency = "NOK"
                },
                interval = new
                {
                    unit = product.Interval == BillingInterval.Monthly ? "MONTH" : "YEAR",
                    count = 1
                },
                merchantRedirectUrl = redirectUrl,
                merchantAgreementUrl = $"{_appOpts.FrontendBaseUrl}/terms",
                productName = product.Name,
                productDescription = product.Description ?? string.Empty,
                phoneNumber
            };

            var request = new HttpRequestMessage(HttpMethod.Post, $"{_opts.BaseUrl}/recurring/v3/agreements");
            AddCommonHeaders(request, token, idempotency: true);
            request.Content = BuildJsonContent(body);

            var response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<VippsCreateAgreementResponse>(json,
                _jsonOpts)!;
        }

        public async Task<VippsAgreementResponse> GetAgreementAsync(string agreementId)
        {
            var token = await GetAccessTokenAsync();

            var request = new HttpRequestMessage(HttpMethod.Get,
                $"{_opts.BaseUrl}/recurring/v3/agreements/{agreementId}");
            AddCommonHeaders(request, token);

            var response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<VippsAgreementResponse>(json,
                _jsonOpts)!;
        }

        public async Task CancelAgreementAsync(string agreementId)
        {
            var token = await GetAccessTokenAsync();

            var request = new HttpRequestMessage(HttpMethod.Patch,
                $"{_opts.BaseUrl}/recurring/v3/agreements/{agreementId}");
            AddCommonHeaders(request, token, idempotency: true);
            request.Content = BuildJsonContent(new { status = "STOPPED" });

            var response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }

        public async Task<VippsCreatePaymentResponse> CreatePaymentAsync(
            Order order, List<VippsOrderLine> receiptLines, string redirectUrl, string phoneNumber)
        {
            var token = await GetAccessTokenAsync();

            var body = new
            {
                amount = new { value = order.TotalInOre, currency = "NOK" },
                paymentMethod = new { type = "WALLET" },
                customer = new { phoneNumber },
                reference = order.Id.ToString(),
                returnUrl = $"{redirectUrl}?orderId={order.Id}",
                userFlow = "WEB_REDIRECT",
                paymentDescription = "Sensor purchase",
                captureType = "RESERVE_CAPTURE",
                receipt = new
                {
                    orderLines = receiptLines.Select(l =>
                    {
                        var lineTotal = l.UnitPriceInOre * l.Quantity;
                        var excludingTax = l.TaxPercentage > 0
                            ? l.UnitPriceExclVatInOre * l.Quantity
                            : lineTotal;
                        return new
                        {
                            name = l.Name,
                            id = l.Id,
                            totalAmount = lineTotal,
                            totalAmountExcludingTax = excludingTax,
                            totalTaxAmount = lineTotal - excludingTax,
                            taxPercentage = l.TaxPercentage,
                            unitInfo = new
                            {
                                unitPrice = l.UnitPriceInOre,
                                quantity = l.Quantity.ToString(),
                                quantityUnit = "PCS"
                            },
                            discount = 0,
                            isReturn = false,
                            isShipping = false
                        };
                    }),
                    bottomLine = new { currency = "NOK", receiptNumber = order.Id.ToString() }
                }
            };

            var request = new HttpRequestMessage(HttpMethod.Post, $"{_opts.BaseUrl}/epayment/v1/payments");
            AddCommonHeaders(request, token, idempotency: true);
            request.Content = BuildJsonContent(body);

            var response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<VippsCreatePaymentResponse>(json,
                _jsonOpts)!;
        }

        public async Task<VippsPaymentResponse> GetPaymentAsync(string reference)
        {
            var token = await GetAccessTokenAsync();

            var request = new HttpRequestMessage(HttpMethod.Get,
                $"{_opts.BaseUrl}/epayment/v1/payments/{reference}");
            AddCommonHeaders(request, token);

            var response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<VippsPaymentResponse>(json,
                _jsonOpts)!;
        }

        public async Task CapturePaymentAsync(string reference, int amountInOre)
        {
            var token = await GetAccessTokenAsync();
            var body = new { modificationAmount = new { value = amountInOre, currency = "NOK" } };
            var request = new HttpRequestMessage(HttpMethod.Post,
                $"{_opts.BaseUrl}/epayment/v1/payments/{reference}/capture");
            AddCommonHeaders(request, token, idempotency: true);
            request.Content = BuildJsonContent(body);
            var response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }

        public async Task CancelPaymentAsync(string reference)
        {
            var token = await GetAccessTokenAsync();
            var request = new HttpRequestMessage(HttpMethod.Post,
                $"{_opts.BaseUrl}/epayment/v1/payments/{reference}/cancel");
            AddCommonHeaders(request, token, idempotency: true);
            request.Content = BuildJsonContent(new { });
            var response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }

        public async Task<(string WebhookId, string Secret)> RegisterWebhookAsync(string url, string[] events)
        {
            var token = await GetAccessTokenAsync();
            var body = new { url, events };
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_opts.BaseUrl}/webhooks/v1/webhooks");
            AddCommonHeaders(request, token, idempotency: true);
            request.Content = BuildJsonContent(body);

            var response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var id = doc.RootElement.GetProperty("id").GetString()!;
            var secret = doc.RootElement.GetProperty("secret").GetString()!;
            return (id, secret);
        }

        public bool VerifyWebhookSignature(string rawBody, string signatureHeader, string secret)
        {
            if (!signatureHeader.StartsWith("sha256=")) return false;
            var receivedHex = signatureHeader["sha256=".Length..];
            var key = Encoding.UTF8.GetBytes(secret);
            var payload = Encoding.UTF8.GetBytes(rawBody);
            var computed = HMACSHA256.HashData(key, payload);
            var computedHex = Convert.ToHexString(computed).ToLowerInvariant();
            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(computedHex),
                Encoding.ASCII.GetBytes(receivedHex.ToLowerInvariant()));
        }

        private void AddCommonHeaders(HttpRequestMessage request, string token, bool idempotency = false)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("Ocp-Apim-Subscription-Key", _opts.SubscriptionKey);
            request.Headers.Add("Merchant-Serial-Number", _opts.MerchantSerialNumber);
            request.Headers.Add("Vipps-System-Name", _systemName);
            request.Headers.Add("Vipps-System-Version", _systemVersion);
            if (idempotency)
                request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        }

        private static StringContent BuildJsonContent(object body) =>
            new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
    }
}
