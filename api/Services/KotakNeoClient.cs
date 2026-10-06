using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NiftyPremiumTrader.Api.Models;
using NiftyPremiumTrader.Api.Options;

namespace NiftyPremiumTrader.Api.Services;

public sealed class KotakNeoClient
{
    private readonly HttpClient _httpClient;
    private readonly KotakNeoOptions _options;

    private string? _viewToken;
    private string? _tradeToken;
    private string? _viewSid;
    private string? _tradeSid;
    private string? _assignedBaseUrl;
    private string? _feedUrl;
    private string? _sfeedUrl;
    private string? _dataCenter;
    private string? _serverId;

    public KotakNeoClient(
        IHttpClientFactory httpClientFactory,
        IOptions<KotakNeoOptions> options)
    {
        _httpClient = httpClientFactory.CreateClient("KotakNeo");
        _options = options.Value;
    }

    public bool IsAuthenticated =>
        !string.IsNullOrWhiteSpace(_tradeToken) &&
        !string.IsNullOrWhiteSpace(_tradeSid);

    private string BaseUrl
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_assignedBaseUrl))
            {
                return _assignedBaseUrl.TrimEnd('/');
            }

            if (_options.Environment.Equals(
                "uat",
                StringComparison.OrdinalIgnoreCase))
            {
                return "https://d-mis.kotaksecurities.com";
            }

            return "https://mis.kotaksecurities.com";
        }
    }

    public async Task LoginWithTotpAsync(
        string totp,
        CancellationToken cancellationToken)
    {
        ValidateLoginConfiguration();

        string loginUrl = BaseUrl + "/login/1.0/tradeApiLogin";

        using var request = CreateRequest(
            HttpMethod.Post,
            loginUrl,
            false);

        var body = new
        {
            mobileNumber = _options.MobileNumber,
            ucc = _options.Ucc,
            totp
        };

        request.Content = new StringContent(
            JsonSerializer.Serialize(body),
            Encoding.UTF8,
            "application/json");

        using JsonDocument response = await SendAsync(
            request,
            cancellationToken);

        if (!response.RootElement.TryGetProperty("data", out JsonElement data))
        {
            throw new InvalidOperationException(
                "Kotak TOTP login did not return the expected data object.");
        }

        _viewToken = GetString(data, "token");
        _viewSid = GetString(data, "sid");

        if (string.IsNullOrWhiteSpace(_viewToken) ||
            string.IsNullOrWhiteSpace(_viewSid))
        {
            throw new InvalidOperationException(
                "Kotak TOTP login did not return a view token/session id.");
        }
    }

    public async Task ValidateMpinAsync(
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_viewToken) ||
            string.IsNullOrWhiteSpace(_viewSid))
        {
            throw new InvalidOperationException(
                "Kotak TOTP login must complete before MPIN validation.");
        }

        string validateUrl = BaseUrl + "/login/1.0/tradeApiValidate";

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            validateUrl);

        request.Headers.TryAddWithoutValidation(
            "Authorization",
            _options.ConsumerKey);

        request.Headers.TryAddWithoutValidation(
            "sid",
            _viewSid);

        request.Headers.TryAddWithoutValidation(
            "Auth",
            _viewToken);

        request.Headers.TryAddWithoutValidation(
            "neo-fin-key",
            _options.NeoFinKey);

        request.Content = new StringContent(
            JsonSerializer.Serialize(new { mpin = _options.Mpin }),
            Encoding.UTF8,
            "application/json");

        using JsonDocument response = await SendAsync(
            request,
            cancellationToken);

        if (!response.RootElement.TryGetProperty("data", out JsonElement data))
        {
            throw new InvalidOperationException(
                "Kotak MPIN validation did not return the expected data object.");
        }

        _tradeToken = GetString(data, "token");
        _tradeSid = GetString(data, "sid");
        _assignedBaseUrl = GetString(data, "baseUrl");
        _feedUrl = GetString(data, "feedUrl");
        _dataCenter = GetString(data, "dataCenter");
        _serverId = GetString(data, "hsServerId") ?? GetString(data, "serverId");

        await ResolveFeedEndpointAsync(cancellationToken);

        if (!IsAuthenticated)
        {
            throw new InvalidOperationException(
                "Kotak MPIN validation did not create a trade session.");
        }
    }


    public string MarketFeedUrl =>
        _sfeedUrl ??
        _feedUrl ??
        "wss://sfeed.kotaksecurities.com/betafeed";

    public KotakNeoMarketFeed CreateMarketFeed(
        MarketFeedOptions? options = null)
    {
        EnsureAuthenticated();

        if (string.IsNullOrWhiteSpace(_options.Ucc) ||
            string.IsNullOrWhiteSpace(_tradeSid))
        {
            throw new InvalidOperationException(
                "Kotak Neo UCC/session id is not available for market feed.");
        }

        return new KotakNeoMarketFeed(
            MarketFeedUrl,
            _options.Ucc,
            _tradeSid,
            options ?? new MarketFeedOptions());
    }

    private async Task ResolveFeedEndpointAsync(
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_dataCenter))
        {
            return;
        }

        try
        {
            string environment = _options.Environment.Equals(
                "uat",
                StringComparison.OrdinalIgnoreCase)
                ? "qa"
                : "prod";

            string configBase = _options.Environment.Equals(
                "uat",
                StringComparison.OrdinalIgnoreCase)
                ? "https://qapi.kotaksecurities.online/5config/config"
                : "https://lapi.kotaksecurities.com/5config/config";

            string url =
                configBase +
                "?appVersion=3.0.1" +
                "&platform=api" +
                $"&environment={environment}";

            using var request = CreateRequest(
                HttpMethod.Get,
                url,
                false);

            using JsonDocument response = await SendAsync(
                request,
                cancellationToken);

            if (!response.RootElement.TryGetProperty(
                    "data",
                    out JsonElement data) ||
                !data.TryGetProperty("configs", out JsonElement configs))
            {
                return;
            }

            if (!configs.TryGetProperty(
                    $"{_dataCenter}_broadcast_source",
                    out JsonElement sourceElement))
            {
                return;
            }

            string? source = sourceElement.GetString();

            if (string.IsNullOrWhiteSpace(source))
            {
                return;
            }

            if (configs.TryGetProperty(
                    $"{_dataCenter}_{source}_broadcast_endpoint",
                    out JsonElement feedElement))
            {
                _sfeedUrl = feedElement.GetString();
            }
        }
        catch
        {
            // The feedUrl returned during MPIN validation or the public
            // Kotak SFeed fallback will be used when config resolution fails.
        }
    }

    public async Task<JsonDocument> ReadLimitsAsync(string path, CancellationToken ct)
    {
        EnsureAuthenticated();
        using var request = CreateRequest(HttpMethod.Post, BaseUrl + path + (string.IsNullOrWhiteSpace(_serverId) ? "" : "?sId=" + Uri.EscapeDataString(_serverId)), true);
        request.Content = CreateForm(("jData", JsonSerializer.Serialize(new {seg="ALL",exch="ALL",prod="ALL"})));
        return await SendAsync(request, ct);
    }
    public async Task<JsonDocument> ReadRequiredMarginAsync(string path, KotakOptionInstrument instrument,
        int quantity, string product, CancellationToken ct)
    {
        EnsureAuthenticated();
        using var request = CreateRequest(HttpMethod.Post, BaseUrl + path + (string.IsNullOrWhiteSpace(_serverId) ? "" : "?sId=" + Uri.EscapeDataString(_serverId)), true);
        request.Content = CreateForm(("jData", JsonSerializer.Serialize(new {exSeg=instrument.ExchangeSegment,prc="0",
            prcTp="MKT",prod=product,qty=quantity.ToString(CultureInfo.InvariantCulture),
            tok=instrument.InstrumentToken,trnsTp="S",trgPrc="0"})));
        return await SendAsync(request, ct);
    }

    public async Task<JsonDocument> PlaceOrderAsync(
        KotakPlaceOrderRequest order,
        CancellationToken cancellationToken)
    {
        EnsureAuthenticated();
        ValidatePlaceOrder(order);

        using var request = CreateRequest(
            HttpMethod.Post,
            BaseUrl + "/quick/order/rule/ms/place",
            true);

        // Kotak Neo v3 removed legacy place-order fields such as
        // market_protection / pf / scrip_token. Sending the old payload can
        // produce stCode=1007 "body invalid". Keep this payload aligned with
        // the current regular-order API.
        request.Content = CreateForm(
            ("am", order.Amo),
            ("dq", order.DisclosedQuantity),
            ("es", order.ExchangeSegment),
            ("pc", order.Product),
            ("pr", order.Price),
            ("pt", order.OrderType),
            ("qt", order.Quantity.ToString(CultureInfo.InvariantCulture)),
            ("rt", order.Validity),
            ("tp", order.TriggerPrice),
            ("ts", order.TradingSymbol),
            ("tt", order.TransactionType),
            ("ig", string.IsNullOrWhiteSpace(order.Tag) ? null : order.Tag),
            ("os", "NEOTRADEAPI"));

        return await SendAsync(
            request,
            cancellationToken);
    }

    public async Task<JsonDocument> GetOrderHistoryAsync(
        string orderId,
        CancellationToken cancellationToken)
    {
        EnsureAuthenticated();

        using var request = CreateRequest(
            HttpMethod.Post,
            BaseUrl + "/quick/order/history",
            true);

        request.Content = CreateForm(("nOrdNo", orderId));

        return await SendAsync(
            request,
            cancellationToken);
    }

    public async Task<JsonDocument> GetOrderBookAsync(
        CancellationToken cancellationToken)
    {
        EnsureAuthenticated();

        using var request = CreateRequest(
            HttpMethod.Get,
            BaseUrl + "/quick/user/orders",
            true);

        return await SendAsync(
            request,
            cancellationToken);
    }

    public async Task<JsonDocument> GetTradeReportAsync(
        CancellationToken cancellationToken)
    {
        EnsureAuthenticated();

        using var request = CreateRequest(
            HttpMethod.Get,
            BaseUrl + "/quick/user/trades",
            true);

        return await SendAsync(
            request,
            cancellationToken);
    }

    public async Task<JsonDocument> GetPositionsAsync(
        CancellationToken cancellationToken)
    {
        EnsureAuthenticated();

        using var request = CreateRequest(
            HttpMethod.Get,
            BaseUrl + "/quick/user/positions",
            true);

        return await SendAsync(
            request,
            cancellationToken);
    }

    public async Task<JsonDocument> GetQuotesAsync(
        IEnumerable<QuoteInstrument> instruments,
        string quoteType,
        CancellationToken cancellationToken)
    {
        List<QuoteInstrument> list = instruments
            .Where(x => !string.IsNullOrWhiteSpace(x.ExchangeSegment))
            .Where(x => !string.IsNullOrWhiteSpace(x.InstrumentToken))
            .Distinct()
            .Take(50)
            .ToList();

        if (list.Count == 0)
        {
            throw new ArgumentException(
                "At least one Kotak quote instrument is required.");
        }

        string symbols = string.Join(
            ",",
            list.Select(x =>
                $"{x.ExchangeSegment}|{x.InstrumentToken}"));

        symbols = Uri.EscapeDataString(symbols)
            .Replace("%7C", "|", StringComparison.OrdinalIgnoreCase)
            .Replace("%2C", ",", StringComparison.OrdinalIgnoreCase);

        string url =
            $"{BaseUrl}/script-details/1.0/quotes/neosymbol/" +
            $"{symbols}/{Uri.EscapeDataString(quoteType)}";

        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Get,
            url,
            false);

        return await SendAsync(
            request,
            cancellationToken);
    }

    public async Task<JsonDocument> GetNiftyOptionChainAsync(
        DateOnly expiryDate,
        CancellationToken cancellationToken)
    {
        string expiry = expiryDate.ToString(
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture);

        string url =
            BaseUrl +
            "/market-data/1.0/watchlist/option-chain" +
            "?exchange=nse_fo" +
            "&underlying=NIFTY" +
            $"&expiry={Uri.EscapeDataString(expiry)}";

        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Get,
            url,
            false);

        return await SendAsync(
            request,
            cancellationToken);
    }

    public async Task<string> DownloadNseFoScripMasterAsync(
        string destination,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Get,
            BaseUrl + "/script-details/1.0/masterscrip/file-paths",
            false);

        using JsonDocument response = await SendAsync(
            request,
            cancellationToken);

        if (!response.RootElement.TryGetProperty("data", out JsonElement data) ||
            !data.TryGetProperty("filesPaths", out JsonElement paths))
        {
            throw new InvalidOperationException(
                "Kotak did not return scrip-master file paths.");
        }

        string? downloadUrl = null;

        foreach (JsonElement item in paths.EnumerateArray())
        {
            string? url = item.GetString();

            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            if (url.Contains(
                "nse_fo",
                StringComparison.OrdinalIgnoreCase))
            {
                downloadUrl = url;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            throw new InvalidOperationException(
                "Kotak NSE F&O scrip-master URL was not found.");
        }

        string? directory = Path.GetDirectoryName(destination);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using HttpResponseMessage downloadResponse = await _httpClient.GetAsync(
            downloadUrl,
            cancellationToken);

        downloadResponse.EnsureSuccessStatusCode();

        await using Stream source = await downloadResponse.Content.ReadAsStreamAsync(
            cancellationToken);

        await using FileStream target = File.Create(destination);

        await source.CopyToAsync(
            target,
            cancellationToken);

        return destination;
    }

    private HttpRequestMessage CreateRequest(
        HttpMethod method,
        string url,
        bool authenticated)
    {
        var request = new HttpRequestMessage(
            method,
            url);

        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        request.Headers.TryAddWithoutValidation(
            "Authorization",
            _options.ConsumerKey);

        request.Headers.TryAddWithoutValidation(
            "neo-fin-key",
            _options.NeoFinKey);

        if (authenticated)
        {
            request.Headers.TryAddWithoutValidation(
                "Sid",
                _tradeSid);

            request.Headers.TryAddWithoutValidation(
                "Auth",
                _tradeToken);
        }

        return request;
    }

    private async Task<JsonDocument> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        string responseBody = await response.Content.ReadAsStringAsync(
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized ||
                response.StatusCode == HttpStatusCode.Forbidden)
            {
                ClearAuthentication();
            }

            throw new InvalidOperationException(
                $"Kotak Neo HTTP {(int)response.StatusCode}: {responseBody}");
        }

        try
        {
            return JsonDocument.Parse(responseBody);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Kotak Neo returned invalid JSON: " + responseBody,
                exception);
        }
    }

    private static void ValidatePlaceOrder(KotakPlaceOrderRequest order)
    {
        string[] exchanges = ["nse_cm", "bse_cm", "nse_fo", "bse_fo", "mcx_fo"];
        string[] products = ["CNC", "NRML", "MIS", "MTF"];
        string[] orderTypes = ["L", "MKT", "SL", "SL-M"];
        string[] transactionTypes = ["B", "S"];
        string[] validities = ["DAY", "IOC"];

        if (!exchanges.Contains(order.ExchangeSegment, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Invalid Kotak exchange segment '{order.ExchangeSegment}'.");
        }

        if (!products.Contains(order.Product, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Invalid Kotak product '{order.Product}'. Use CNC, NRML, MIS or MTF.");
        }

        if (!orderTypes.Contains(order.OrderType, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Invalid Kotak order type '{order.OrderType}'. Use L, MKT, SL or SL-M.");
        }

        if (!transactionTypes.Contains(order.TransactionType, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Invalid Kotak transaction type '{order.TransactionType}'. Use B or S.");
        }

        if (!validities.Contains(order.Validity, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Invalid Kotak validity '{order.Validity}'. Use DAY or IOC.");
        }

        if (order.Quantity <= 0)
        {
            throw new ArgumentException("Kotak order quantity must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(order.TradingSymbol))
        {
            throw new ArgumentException("Kotak trading symbol is required.");
        }

        if ((order.OrderType == "L" || order.OrderType == "SL") &&
            (!decimal.TryParse(
                order.Price,
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out decimal price) ||
             price <= 0m))
        {
            throw new ArgumentException("A positive price is required for Kotak L/SL orders.");
        }
    }

    private void ValidateLoginConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_options.ConsumerKey))
        {
            throw new InvalidOperationException(
                "KotakNeo:ConsumerKey is not configured.");
        }

        if (string.IsNullOrWhiteSpace(_options.MobileNumber))
        {
            throw new InvalidOperationException(
                "KotakNeo:MobileNumber is not configured.");
        }

        if (string.IsNullOrWhiteSpace(_options.Ucc))
        {
            throw new InvalidOperationException(
                "KotakNeo:Ucc is not configured.");
        }

        if (string.IsNullOrWhiteSpace(_options.Mpin))
        {
            throw new InvalidOperationException(
                "KotakNeo:Mpin is not configured.");
        }
    }

    private void EnsureAuthenticated()
    {
        if (!IsAuthenticated)
        {
            throw new InvalidOperationException(
                "Kotak Neo is not authenticated.");
        }
    }

    private void ClearAuthentication()
    {
        _viewToken = null;
        _tradeToken = null;
        _viewSid = null;
        _tradeSid = null;
        _assignedBaseUrl = null;
        _feedUrl = null;
        _sfeedUrl = null;
        _dataCenter = null;
        _serverId = null;
    }

    private static FormUrlEncodedContent CreateForm(
        params (string Name, string? Value)[] values)
    {
        var content = new List<KeyValuePair<string, string>>();

        foreach ((string name, string? value) in values)
        {
            if (value == null)
            {
                continue;
            }

            content.Add(
                new KeyValuePair<string, string>(
                    name,
                    value));
        }

        return new FormUrlEncodedContent(content);
    }

    private static string? GetString(
        JsonElement element,
        string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.ToString();
    }
}

public sealed class KotakPlaceOrderRequest
{
    public string ExchangeSegment { get; set; } = "nse_fo";
    public string Product { get; set; } = "MIS";
    public string OrderType { get; set; } = "MKT";
    public string Price { get; set; } = "0";
    public int Quantity { get; set; }
    public string TradingSymbol { get; set; } = string.Empty;
    public string TransactionType { get; set; } = "B";
    public string Tag { get; set; } = "NIFTY_PREMIUM";
    public string Amo { get; set; } = "NO";
    public string DisclosedQuantity { get; set; } = "0";
    public string Validity { get; set; } = "DAY";
    public string TriggerPrice { get; set; } = "0";
}
