using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NiftyPremiumTrader.Api.Data;
using NiftyPremiumTrader.Api.Options;

namespace NiftyPremiumTrader.Api.Services;

public sealed class BreezeRestClient
{
    private const string CustomerDetailsUrl =
        "https://api.icicidirect.com/breezeapi/api/v1/customerdetails";

    private const string V1BaseUrl =
        "https://api.icicidirect.com/breezeapi/api/v1/";


    private readonly HttpClient _httpClient;
    private readonly CandleRepository _repository;
    private readonly ApiBudgetService _budgetService;
    private readonly BreezeOptions _options;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    private string? _rawSessionUsed;
    private string? _resolvedSessionToken;

    public BreezeRestClient(
        IHttpClientFactory httpClientFactory,
        CandleRepository repository,
        ApiBudgetService budgetService,
        IOptions<BreezeOptions> options)
    {
        _httpClient = httpClientFactory.CreateClient("Breeze");
        _repository = repository;
        _budgetService = budgetService;
        _options = options.Value;
    }

    public async Task<JToken> GetHistoricalAsync(
        string interval,
        string fromDate,
        string toDate,
        string stockCode,
        string exchangeCode,
        string productType,
        string expiryDate,
        string right,
        string strikePrice,
        bool livePriority,
        CancellationToken cancellationToken)
    {
        // Equivalent of the attached IciciDirect.getHistoricalData wrapper:
        // signed V1 GET JSON body (not historical V2 query parameters).
        var payload = new JObject
        {
            ["interval"] = interval, ["from_date"] = fromDate, ["to_date"] = toDate,
            ["stock_code"] = stockCode, ["exchange_code"] = exchangeCode,
            ["product_type"] = productType
        };
        if (!string.IsNullOrEmpty(expiryDate)) payload["expiry_date"] = expiryDate;
        if (!string.IsNullOrEmpty(right)) payload["right"] = right;
        if (!string.IsNullOrEmpty(strikePrice)) payload["strike_price"] = strikePrice;
        return await SendSignedV1Async(HttpMethod.Get, "historicalcharts", payload, livePriority, cancellationToken);
    }

    public async Task<JToken> SendSignedV1Async(
        HttpMethod method,
        string endpoint,
        JObject payload,
        bool livePriority,
        CancellationToken cancellationToken)
    {
        await _budgetService.ConsumeAsync(
            1,
            livePriority,
            cancellationToken);

        string sessionToken = await GetResolvedSessionTokenAsync(
            livePriority,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(_options.ApiSecret))
        {
            throw new InvalidOperationException(
                "Breeze:ApiSecret is not configured.");
        }

        string payloadText = payload.ToString(Formatting.None);

        string timestamp = DateTime.UtcNow.ToString(
            "yyyy-MM-ddTHH:mm:ss.000Z",
            CultureInfo.InvariantCulture);

        string checksumInput =
            timestamp + payloadText + _options.ApiSecret;

        byte[] checksumBytes = SHA256.HashData(
            Encoding.UTF8.GetBytes(checksumInput));

        string checksum = Convert.ToHexString(checksumBytes)
            .ToLowerInvariant();

        string url = V1BaseUrl + endpoint.TrimStart('/');

        using var request = new HttpRequestMessage(
            method,
            url);

        request.Content = new StringContent(
            payloadText,
            Encoding.UTF8,
            "application/json");

        request.Headers.TryAddWithoutValidation(
            "X-Checksum",
            "token " + checksum);

        request.Headers.TryAddWithoutValidation(
            "X-Timestamp",
            timestamp);

        request.Headers.TryAddWithoutValidation(
            "X-AppKey",
            _options.ApiKey);

        request.Headers.TryAddWithoutValidation(
            "X-SessionToken",
            sessionToken);

        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            cancellationToken);

        string responseText = await response.Content.ReadAsStringAsync(
            cancellationToken);

        return ParseResponseOrThrow(
            response,
            responseText,
            $"Breeze {endpoint} request");
    }

    private async Task<string> GetResolvedSessionTokenAsync(
        bool livePriority,
        CancellationToken cancellationToken)
    {
        string rawSession = await ResolveRawSessionAsync(
            cancellationToken);

        if (_resolvedSessionToken != null &&
            _rawSessionUsed == rawSession)
        {
            return _resolvedSessionToken;
        }

        await _sessionLock.WaitAsync(cancellationToken);

        try
        {
            if (_resolvedSessionToken != null &&
                _rawSessionUsed == rawSession)
            {
                return _resolvedSessionToken;
            }

            await _budgetService.ConsumeAsync(
                1,
                livePriority,
                cancellationToken);

            var payload = new JObject
            {
                ["SessionToken"] = rawSession,
                ["AppKey"] = _options.ApiKey
            };

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                CustomerDetailsUrl);

            request.Content = new StringContent(
                payload.ToString(Formatting.None),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                cancellationToken);

            string responseText = await response.Content.ReadAsStringAsync(
                cancellationToken);

            JToken token = ParseResponseOrThrow(
                response,
                responseText,
                "Breeze customer-details request");

            string? resolved = token["Success"]?["session_token"]?.ToString();

            if (string.IsNullOrWhiteSpace(resolved))
            {
                throw new InvalidOperationException(
                    "Breeze customer-details response did not contain Success.session_token.");
            }

            _rawSessionUsed = rawSession;
            _resolvedSessionToken = resolved;

            return resolved;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task<string> ResolveRawSessionAsync(
        CancellationToken cancellationToken)
    {
        if (_options.UseSessionMaster)
        {
            string? databaseSession = await _repository.GetIciciSessionAsync(
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(databaseSession))
            {
                return databaseSession;
            }
        }

        if (!string.IsNullOrWhiteSpace(_options.SessionToken))
        {
            return _options.SessionToken;
        }

        throw new InvalidOperationException(
            "No Breeze API session was found. " +
            "Set Breeze:SessionToken or populate sessionmaster.icicisession.");
    }

    private static JToken ParseResponseOrThrow(
        HttpResponseMessage response,
        string responseText,
        string operation)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            throw new InvalidOperationException(
                operation + " returned an empty response.");
        }

        JToken token;

        try
        {
            token = JToken.Parse(responseText);
        }
        catch (JsonReaderException ex)
        {
            throw new InvalidOperationException(
                operation + " returned invalid JSON: " + responseText,
                ex);
        }

        string? apiError = token["Error"]?.ToString();
        int? apiStatus = token["Status"]?.Value<int?>();

        if (!response.IsSuccessStatusCode ||
            (apiStatus.HasValue && apiStatus.Value != 200) ||
            !string.IsNullOrWhiteSpace(apiError))
        {
            throw new InvalidOperationException(
                operation +
                $" failed. HTTP={(int)response.StatusCode}, " +
                $"Status={apiStatus?.ToString() ?? "unknown"}, " +
                $"Error={apiError ?? "unknown"}. Response: {responseText}");
        }

        return token;
    }

    private static string BuildQueryString(
        IReadOnlyDictionary<string, string> values)
    {
        var parts = new List<string>();

        foreach (KeyValuePair<string, string> item in values)
        {
            string name = Uri.EscapeDataString(item.Key);
            string value = Uri.EscapeDataString(item.Value ?? string.Empty);
            parts.Add(name + "=" + value);
        }

        return string.Join("&", parts);
    }
}
