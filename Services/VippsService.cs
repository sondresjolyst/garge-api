using garge_api.Models;
using garge_api.Models.Shop;
using garge_api.Models.Subscription;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace garge_api.Services
{
    public class VippsService : IVippsService
    {
        private readonly HttpClient _http;
        private readonly VippsOptions _opts;
        private readonly AppOptions _appOpts;
        private readonly IMemoryCache _cache;
        private readonly ILogger<VippsService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly string _systemName;
        private readonly string _systemVersion;

        private const string TestModeCacheKey = "vipps_test_mode";
        private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };
        private static readonly TimeSpan ReplayWindow = TimeSpan.FromMinutes(5);
        private static readonly Regex SigRegex = new(@"Signature=([^,&\s]+)", RegexOptions.Compiled);

        private readonly record struct VippsEffective(string BaseUrl, string Token, string SubscriptionKey, string Msn);

        public VippsService(
            HttpClient http,
            IOptions<VippsOptions> opts,
            IOptions<AppOptions> appOpts,
            IMemoryCache cache,
            ILogger<VippsService> logger,
            IServiceScopeFactory scopeFactory)
        {
            _http = http;
            _opts = opts.Value;
            _appOpts = appOpts.Value;
            _cache = cache;
            _logger = logger;
            _scopeFactory = scopeFactory;

            var assembly = Assembly.GetExecutingAssembly();
            _systemName = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "garge";
            _systemVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                             ?? assembly.GetName().Version?.ToString()
                             ?? "1.0.0";
        }

        private async Task<bool> IsTestModeAsync()
        {
            if (_cache.TryGetValue(TestModeCacheKey, out bool cached))
                return cached;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var settings = await db.AppSettings.FindAsync(1);
            var isTest = settings?.VippsTestMode ?? false;
            _cache.Set(TestModeCacheKey, isTest, TimeSpan.FromSeconds(30));
            return isTest;
        }

        private async Task<VippsEffective> GetEffectiveAsync()
        {
            var isTest = await IsTestModeAsync();

            string baseUrl, clientId, clientSecret, msn, subKey, cacheKey;
            if (isTest)
            {
                baseUrl      = _opts.TestBaseUrl;
                clientId     = _opts.TestClientId;
                clientSecret = _opts.TestClientSecret;
                msn          = _opts.TestMerchantSerialNumber;
                subKey       = _opts.TestSubscriptionKey;
                cacheKey     = "vipps_token_test";
            }
            else
            {
                baseUrl      = _opts.BaseUrl;
                clientId     = _opts.ClientId;
                clientSecret = _opts.ClientSecret;
                msn          = _opts.MerchantSerialNumber;
                subKey       = _opts.SubscriptionKey;
                cacheKey     = "vipps_token_live";
            }

            if (!_cache.TryGetValue(cacheKey, out string? token) || token == null)
                token = await FetchTokenAsync(baseUrl, clientId, clientSecret, subKey, cacheKey);

            return new VippsEffective(baseUrl, token, subKey, msn);
        }

        private async Task<string> FetchTokenAsync(
            string baseUrl, string clientId, string clientSecret, string subscriptionKey, string cacheKey)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/accesstoken/get");
            request.Headers.Add("client_id", clientId);
            request.Headers.Add("client_secret", clientSecret);
            request.Headers.Add("Ocp-Apim-Subscription-Key", subscriptionKey);
            request.Content = new StringContent(string.Empty, Encoding.UTF8, "application/json");

            var response = await _http.SendAsync(request);
            var json = await ReadAsStringAndEnsureSuccessAsync(response, "accesstoken/get");
            using var doc = JsonDocument.Parse(json);
            var token = doc.RootElement.GetProperty("access_token").GetString()!;

            var expiresIn = 3600;
            if (doc.RootElement.TryGetProperty("expires_in", out var exp))
            {
                var expStr = exp.ValueKind == JsonValueKind.String ? exp.GetString() : exp.GetRawText();
                if (!int.TryParse(expStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out expiresIn))
                    expiresIn = 3600;
            }

            _cache.Set(cacheKey, token, TimeSpan.FromSeconds(Math.Max(60, expiresIn - 60)));
            _logger.LogInformation("Vipps access token refreshed ({CacheKey}), expires in {ExpiresIn}s", cacheKey, expiresIn);
            return token;
        }

        public async Task<string> GetAccessTokenAsync()
        {
            return (await GetEffectiveAsync()).Token;
        }

        public async Task<VippsCreateAgreementResponse> CreateAgreementAsync(
            Product product, string userId, string redirectUrl, string phoneNumber,
            int effectivePriceInOre, string idempotencyKey)
        {
            var e = await GetEffectiveAsync();

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

            var request = new HttpRequestMessage(HttpMethod.Post, $"{e.BaseUrl}/recurring/v3/agreements");
            AddCommonHeaders(request, e, idempotencyKey);
            request.Content = BuildJsonContent(body);

            var response = await _http.SendAsync(request);
            var json = await ReadAsStringAndEnsureSuccessAsync(response, "create-agreement");
            return JsonSerializer.Deserialize<VippsCreateAgreementResponse>(json, _jsonOpts)!;
        }

        public async Task<VippsAgreementResponse> GetAgreementAsync(string agreementId)
        {
            var e = await GetEffectiveAsync();

            var request = new HttpRequestMessage(HttpMethod.Get,
                $"{e.BaseUrl}/recurring/v3/agreements/{agreementId}");
            AddCommonHeaders(request, e);

            var response = await _http.SendAsync(request);
            var json = await ReadAsStringAndEnsureSuccessAsync(response, "get-agreement");
            return JsonSerializer.Deserialize<VippsAgreementResponse>(json, _jsonOpts)!;
        }

        public async Task CancelAgreementAsync(string agreementId, string idempotencyKey)
        {
            var e = await GetEffectiveAsync();

            var request = new HttpRequestMessage(HttpMethod.Patch,
                $"{e.BaseUrl}/recurring/v3/agreements/{agreementId}");
            AddCommonHeaders(request, e, idempotencyKey);
            request.Content = BuildJsonContent(new { status = "STOPPED" });

            var response = await _http.SendAsync(request);
            await ReadAsStringAndEnsureSuccessAsync(response, "cancel-agreement");
        }

        public async Task<VippsCreatePaymentResponse> CreatePaymentAsync(
            Order order, List<VippsOrderLine> receiptLines, string redirectUrl,
            string phoneNumber, string idempotencyKey)
        {
            var e = await GetEffectiveAsync();

            var reference = $"garge-order-{order.Id:D6}";
            var body = new
            {
                amount = new { value = order.TotalInOre, currency = "NOK" },
                paymentMethod = new { type = "WALLET" },
                customer = new { phoneNumber },
                reference,
                returnUrl = $"{redirectUrl}?orderId={order.Id}",
                userFlow = "WEB_REDIRECT",
                paymentDescription = $"Garge order #{order.Id}",
                captureType = "RESERVE_CAPTURE",
                receipt = new
                {
                    orderLines = receiptLines.Select(l =>
                    {
                        var lineTotal = l.UnitPriceInOre * l.Quantity;
                        var excludingTax = l.TaxPercentageBasisPoints > 0
                            ? l.UnitPriceExclVatInOre * l.Quantity
                            : lineTotal;
                        return new
                        {
                            name = l.Name,
                            id = l.Id,
                            totalAmount = lineTotal,
                            totalAmountExcludingTax = excludingTax,
                            totalTaxAmount = lineTotal - excludingTax,
                            taxPercentage = l.TaxPercentageBasisPoints,
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
                    bottomLine = new { currency = "NOK", receiptNumber = reference }
                }
            };

            var request = new HttpRequestMessage(HttpMethod.Post, $"{e.BaseUrl}/epayment/v1/payments");
            AddCommonHeaders(request, e, idempotencyKey);
            request.Content = BuildJsonContent(body);

            var response = await _http.SendAsync(request);
            var json = await ReadAsStringAndEnsureSuccessAsync(response, "create-payment");
            return JsonSerializer.Deserialize<VippsCreatePaymentResponse>(json, _jsonOpts)!;
        }

        public async Task<VippsPaymentResponse> GetPaymentAsync(string reference)
        {
            var e = await GetEffectiveAsync();

            var request = new HttpRequestMessage(HttpMethod.Get,
                $"{e.BaseUrl}/epayment/v1/payments/{reference}");
            AddCommonHeaders(request, e);

            var response = await _http.SendAsync(request);
            var json = await ReadAsStringAndEnsureSuccessAsync(response, "get-payment");
            return JsonSerializer.Deserialize<VippsPaymentResponse>(json, _jsonOpts)!;
        }

        public async Task CapturePaymentAsync(string reference, int amountInOre, string idempotencyKey)
        {
            var e = await GetEffectiveAsync();
            var body = new { modificationAmount = new { value = amountInOre, currency = "NOK" } };
            var request = new HttpRequestMessage(HttpMethod.Post,
                $"{e.BaseUrl}/epayment/v1/payments/{reference}/capture");
            AddCommonHeaders(request, e, idempotencyKey);
            request.Content = BuildJsonContent(body);
            var response = await _http.SendAsync(request);
            await ReadAsStringAndEnsureSuccessAsync(response, "capture-payment");
        }

        public async Task CancelPaymentAsync(string reference, string idempotencyKey)
        {
            var e = await GetEffectiveAsync();
            var request = new HttpRequestMessage(HttpMethod.Post,
                $"{e.BaseUrl}/epayment/v1/payments/{reference}/cancel");
            AddCommonHeaders(request, e, idempotencyKey);
            request.Content = BuildJsonContent(new { });
            var response = await _http.SendAsync(request);
            await ReadAsStringAndEnsureSuccessAsync(response, "cancel-payment");
        }

        public async Task<(string WebhookId, string Secret)> RegisterWebhookAsync(string url, string[] events)
        {
            var e = await GetEffectiveAsync();
            var body = new { url, events };
            var request = new HttpRequestMessage(HttpMethod.Post, $"{e.BaseUrl}/webhooks/v1/webhooks");
            AddCommonHeaders(request, e, Guid.NewGuid().ToString());
            request.Content = BuildJsonContent(body);

            var response = await _http.SendAsync(request);
            var json = await ReadAsStringAndEnsureSuccessAsync(response, "register-webhook");
            using var doc = JsonDocument.Parse(json);
            var id = doc.RootElement.GetProperty("id").GetString()!;
            var secret = doc.RootElement.GetProperty("secret").GetString()!;
            return (id, secret);
        }

        public WebhookVerifyResult VerifyWebhookSignature(HttpRequest request, string rawBody, string secret)
        {
            if (string.IsNullOrEmpty(secret))
                return WebhookVerifyResult.MissingSecret;

            var auth = request.Headers["Authorization"].ToString();
            if (string.IsNullOrEmpty(auth) || !auth.StartsWith("HMAC-SHA256 ", StringComparison.Ordinal))
                return WebhookVerifyResult.MissingHeader;

            var sigMatch = SigRegex.Match(auth);
            if (!sigMatch.Success) return WebhookVerifyResult.MissingHeader;
            var receivedSig = sigMatch.Groups[1].Value;

            var dateHeader = request.Headers["x-ms-date"].ToString();
            var contentHashHeader = request.Headers["x-ms-content-sha256"].ToString();
            if (string.IsNullOrEmpty(dateHeader) || string.IsNullOrEmpty(contentHashHeader))
                return WebhookVerifyResult.MissingHeader;

            // Replay protection: x-ms-date must be within 5 min window
            if (!DateTimeOffset.TryParse(dateHeader, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var sentAt))
                return WebhookVerifyResult.BadDate;
            var skew = DateTimeOffset.UtcNow - sentAt;
            if (skew.Duration() > ReplayWindow)
                return WebhookVerifyResult.Stale;

            // Content hash check
            var bodyHashBase64 = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(rawBody)));
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(bodyHashBase64),
                    Encoding.ASCII.GetBytes(contentHashHeader)))
                return WebhookVerifyResult.BadContentHash;

            // Canonical: METHOD\n<pathAndQuery>\n<x-ms-date>;<host>;<x-ms-content-sha256>
            var pathAndQuery = request.Path.Value + request.QueryString.Value;
            var host = request.Headers["Host"].ToString();
            if (string.IsNullOrEmpty(host)) host = request.Host.Value ?? string.Empty;
            var canonical = $"{request.Method}\n{pathAndQuery}\n{dateHeader};{host};{contentHashHeader}";

            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var computed = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)));

            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(computed),
                Encoding.ASCII.GetBytes(receivedSig))
                ? WebhookVerifyResult.Valid
                : WebhookVerifyResult.BadSignature;
        }

        private void AddCommonHeaders(HttpRequestMessage request, VippsEffective e, string? idempotencyKey = null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", e.Token);
            request.Headers.Add("Ocp-Apim-Subscription-Key", e.SubscriptionKey);
            request.Headers.Add("Merchant-Serial-Number", e.Msn);
            request.Headers.Add("Vipps-System-Name", _systemName);
            request.Headers.Add("Vipps-System-Version", _systemVersion);
            if (!string.IsNullOrEmpty(idempotencyKey))
                request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        private static StringContent BuildJsonContent(object body) =>
            new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        private async Task<string> ReadAsStringAndEnsureSuccessAsync(HttpResponseMessage response, string operation)
        {
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Vipps {Operation} failed: {Status} {Body}",
                    operation, (int)response.StatusCode, body);
                response.EnsureSuccessStatusCode();
            }
            return body;
        }
    }
}
