using System.Globalization;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using NiftyPremiumTrader.Api.Models;
using NiftyPremiumTrader.Api.Options;

namespace NiftyPremiumTrader.Api.Services;

public sealed class BreezeMarketDataClient : IBreezeDaySource
{
    private readonly BreezeRestClient _restClient;
    private readonly BreezeOptions _options;

    public BreezeMarketDataClient(
        BreezeRestClient restClient,
        IOptions<BreezeOptions> options)
    {
        _restClient = restClient;
        _options = options.Value;
    }

    public async Task<List<OptionCandle>> GetOptionDayAsync(
        DateOnly sessionDate,
        DateOnly expiryDate,
        decimal strike,
        string optionType,
        bool livePriority,
        CancellationToken cancellationToken)
    {
        string fromDate = sessionDate.ToString(
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture) + "T03:45:00.000Z";

        string toDate = BuildToDate(
            sessionDate,
            livePriority);

        string right = optionType.Equals(
            "CE",
            StringComparison.OrdinalIgnoreCase)
            ? "call"
            : "put";

        JToken response = await _restClient.GetHistoricalAsync(
            "5minute",
            fromDate,
            toDate,
            _options.StockCode,
            _options.ExchangeCode,
            "options",
            FormatExpiry(expiryDate),
            right,
            strike.ToString("0.##", CultureInfo.InvariantCulture),
            livePriority,
            cancellationToken);

        List<OptionCandle> candles = ParseHistoricalResponse(
            response,
            sessionDate,
            expiryDate,
            strike,
            optionType.ToUpperInvariant(),
            _options.ExchangeCode,
            "options");

        return FilterUsableCandles(
            candles,
            sessionDate,
            livePriority);
    }

    public async Task<List<OptionCandle>> GetIndexDayAsync(
        DateOnly sessionDate,
        bool livePriority,
        CancellationToken cancellationToken)
    {
        string fromDate = sessionDate.ToString(
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture) + "T03:45:00.000Z";

        string toDate = BuildToDate(
            sessionDate,
            livePriority);

        JToken response = await _restClient.GetHistoricalAsync(
            "5minute",
            fromDate,
            toDate,
            _options.StockCode,
            _options.IndexExchangeCode,
            "cash",
            string.Empty,
            string.Empty,
            string.Empty,
            livePriority,
            cancellationToken);

        List<OptionCandle> candles = ParseHistoricalResponse(
            response,
            sessionDate,
            null,
            null,
            null,
            _options.IndexExchangeCode,
            "cash");

        return FilterUsableCandles(
            candles,
            sessionDate,
            livePriority);
    }

    public async Task<decimal> GetOptionQuoteAsync(
        DateOnly expiryDate,
        decimal strike,
        string optionType,
        bool livePriority,
        CancellationToken cancellationToken)
    {
        string right = optionType.Equals(
            "CE",
            StringComparison.OrdinalIgnoreCase)
            ? "call"
            : "put";

        var payload = new JObject
        {
            ["stock_code"] = _options.StockCode,
            ["exchange_code"] = _options.ExchangeCode,
            ["expiry_date"] = FormatExpiry(expiryDate),
            ["product_type"] = "options",
            ["right"] = right,
            ["strike_price"] = strike.ToString(
                "0.##",
                CultureInfo.InvariantCulture)
        };

        JToken response = await _restClient.SendSignedV1Async(
            HttpMethod.Get,
            "quotes",
            payload,
            livePriority,
            cancellationToken);

        JToken? success = FindProperty(response, "Success") ?? response;

        JToken? ltp = FindFirstNumericProperty(
            success,
            "ltp",
            "last",
            "last_price",
            "lastprice");

        if (ltp == null)
        {
            throw new InvalidOperationException(
                "Breeze quote response did not contain an option LTP.");
        }

        return ltp.Value<decimal>();
    }

    private List<OptionCandle> ParseHistoricalResponse(
        JToken response,
        DateOnly sessionDate,
        DateOnly? expiryDate,
        decimal? strike,
        string? optionType,
        string exchangeCode,
        string productType)
    {
        JToken? success = FindProperty(response, "Success");

        if (success == null)
        {
            JToken? error = FindProperty(response, "Error");
            JToken? status = FindProperty(response, "Status");

            string errorText = error?.ToString() ?? "Unknown Breeze error";
            string statusText = status?.ToString() ?? "unknown";

            throw new InvalidOperationException(
                $"Breeze historical-data request failed. Status={statusText}, Error={errorText}");
        }

        if (success.Type == JTokenType.String)
        {
            string text = success.Value<string>() ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(text))
            {
                success = JToken.Parse(text);
            }
        }

        if (success is not JArray rows)
        {
            throw new InvalidOperationException(
                "Breeze historical-data response did not contain a candle array in Success.");
        }

        var candles = new List<OptionCandle>();

        foreach (JToken row in rows)
        {
            string? dateText = Value(row, "datetime");

            if (string.IsNullOrWhiteSpace(dateText))
            {
                continue;
            }

            DateTimeOffset candleTime = ParseBreezeTime(dateText);

            var candle = new OptionCandle
            {
                SessionDate = sessionDate,
                CandleTime = candleTime,
                StockCode = _options.StockCode,
                ExchangeCode = exchangeCode,
                ProductType = productType,
                ExpiryDate = expiryDate,
                Strike = strike,
                OptionType = optionType,
                Open = DecimalValue(row, "open"),
                High = DecimalValue(row, "high"),
                Low = DecimalValue(row, "low"),
                Close = DecimalValue(row, "close"),
                Volume = LongValue(row, "volume"),
                OpenInterest = LongValue(row, "open_interest"),
                IsComplete = candleTime.AddMinutes(5) <= DateTimeOffset.UtcNow,
                DataSource = "BREEZE_V1_UTC_DAY"
            };

            if (candle.Open <= 0m ||
                candle.High <= 0m ||
                candle.Low <= 0m ||
                candle.Close <= 0m)
            {
                continue;
            }

            candles.Add(candle);
        }

        return candles;
    }

    private static List<OptionCandle> FilterUsableCandles(
        IEnumerable<OptionCandle> candles,
        DateOnly sessionDate,
        bool livePriority)
    {
        TimeOnly marketStart = new(9, 15);
        TimeOnly marketEnd = new(15, 30);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        var result = new List<OptionCandle>();

        foreach (OptionCandle candle in candles)
        {
            DateTimeOffset indiaTime = MarketClock.ToIndia(
                candle.CandleTime);

            DateOnly candleDate = DateOnly.FromDateTime(
                indiaTime.DateTime);

            TimeOnly candleTime = TimeOnly.FromDateTime(
                indiaTime.DateTime);

            if (candleDate != sessionDate)
            {
                continue;
            }

            if (candleTime < marketStart || candleTime > marketEnd)
            {
                continue;
            }

            if (candle.CandleTime > now)
            {
                continue;
            }

            result.Add(candle);
        }

        return result
            .GroupBy(c=>c.CandleTime).Select(g=>g.Last())
            .OrderBy(x => x.CandleTime)
            .ToList();
    }

    private static JToken? FindProperty(
        JToken token,
        string name)
    {
        if (token is JObject obj)
        {
            JProperty? property = obj.Properties()
                .FirstOrDefault(x => x.Name.Equals(
                    name,
                    StringComparison.OrdinalIgnoreCase));

            if (property != null)
            {
                return property.Value;
            }
        }

        foreach (JToken child in token.Children())
        {
            JToken? found = FindProperty(child, name);

            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private static JToken? FindFirstNumericProperty(
        JToken token,
        params string[] names)
    {
        foreach (string name in names)
        {
            JToken? found = FindProperty(token, name);

            if (found == null)
            {
                continue;
            }

            if (decimal.TryParse(
                found.ToString(),
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out decimal value))
            {
                return new JValue(value);
            }
        }

        return null;
    }

    private static string? Value(
        JToken row,
        string name)
    {
        JToken? direct = row[name];

        if (direct != null)
        {
            return direct.ToString();
        }

        if (row is not JObject obj)
        {
            return null;
        }

        JProperty? property = obj.Properties()
            .FirstOrDefault(x => x.Name.Equals(
                name,
                StringComparison.OrdinalIgnoreCase));

        return property?.Value.ToString();
    }

    private static decimal DecimalValue(
        JToken row,
        string name)
    {
        string? text = Value(row, name);

        if (decimal.TryParse(
            text,
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out decimal value))
        {
            return value;
        }

        return 0m;
    }

    private static long LongValue(
        JToken row,
        string name)
    {
        string? text = Value(row, name);

        if (long.TryParse(
            text,
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out long value))
        {
            return value;
        }

        return 0L;
    }

    public static DateTimeOffset ParseBreezeTime(string value)
    {
        string text = value.Trim();

        // V1 request dates are UTC. Offset-less response datetime values are
        // the Indian market clock in the supplied TickData export; normalize
        // once for PostgreSQL. Explicit response offsets are respected.
        if (HasExplicitOffset(text) &&
            DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out DateTimeOffset offsetTime))
        {
            return offsetTime.ToUniversalTime();
        }

        DateTime indiaTime = DateTime.Parse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces);

        return MarketClock.ToUtc(indiaTime);
    }

    private static bool HasExplicitOffset(string value)
    {
        if (value.EndsWith("Z", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        int timeSeparator = value.IndexOf('T');

        if (timeSeparator < 0)
        {
            timeSeparator = value.IndexOf(' ');
        }

        if (timeSeparator < 0)
        {
            return false;
        }

        string timePart = value[(timeSeparator + 1)..];

        return timePart.Contains('+') ||
               timePart.LastIndexOf('-') > 1;
    }

    public static string BuildToDate(DateOnly sessionDate, bool livePriority)
    {
        // Always request the full date, also in live mode. The API returns the
        // portion generated so far; forming bars are stored but never signal.
        return sessionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T10:30:00.000Z";
    }

    private static string FormatExpiry(DateOnly value)
    {
        return value.ToString(
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture) + "T00:00:00.000Z";
    }
}
