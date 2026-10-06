using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using NiftyPremiumTrader.Api.Options;

namespace NiftyPremiumTrader.Api.Services;

public sealed class KotakOptionInstrument
{
    public string InstrumentToken { get; set; } = string.Empty;
    public string TradingSymbol { get; set; } = string.Empty;
    public string ExchangeSegment { get; set; } = "nse_fo";
    public DateOnly ExpiryDate { get; set; }
    public decimal Strike { get; set; }
    public string OptionType { get; set; } = string.Empty;
    public int LotSize { get; set; }
}

public sealed class KotakInstrumentService
{
    private readonly KotakNeoClient _client;
    private readonly KotakAuthenticationService _authenticationService;
    private readonly KotakNeoOptions _options;
    private readonly SemaphoreSlim _masterGate = new(1, 1);

    private List<KotakOptionInstrument> _instruments = [];
    private DateOnly? _loadedDate;

    public KotakInstrumentService(
        KotakNeoClient client,
        KotakAuthenticationService authenticationService,
        IOptions<KotakNeoOptions> options)
    {
        _client = client;
        _authenticationService = authenticationService;
        _options = options.Value;
    }

    public async Task<KotakOptionInstrument?> TryResolveByTokenOrSymbolAsync(
        string instrumentToken,
        string tradingSymbol,
        CancellationToken cancellationToken)
    {
        await EnsureMasterLoadedAsync(false, cancellationToken);

        KotakOptionInstrument? instrument = FindByTokenOrSymbol(
            instrumentToken,
            tradingSymbol);

        if (instrument != null)
        {
            return instrument;
        }

        await EnsureMasterLoadedAsync(true, cancellationToken);

        return FindByTokenOrSymbol(
            instrumentToken,
            tradingSymbol);
    }

    public async Task<KotakOptionInstrument?> TryResolveAsync(
        DateOnly expiryDate,
        decimal strike,
        string optionType,
        CancellationToken cancellationToken)
    {
        string normalizedType = NormalizeOptionType(optionType);

        // Prefer Kotak's current option-chain API for live order resolution.
        // It directly returns neoSymbol/token, trading symbol, strike and market lot
        // and avoids the known inconsistencies in some daily scrip-master expiry fields.
        KotakOptionInstrument? fromChain = await TryResolveFromOptionChainAsync(
            expiryDate,
            strike,
            normalizedType,
            cancellationToken);

        if (fromChain != null)
        {
            CacheInstrument(fromChain);
            return fromChain;
        }

        await EnsureMasterLoadedAsync(false, cancellationToken);

        KotakOptionInstrument? instrument = FindContract(
            expiryDate,
            strike,
            normalizedType);

        if (instrument != null)
        {
            return instrument;
        }

        // Re-download once on a miss so a stale local CSV cannot block a valid order.
        await EnsureMasterLoadedAsync(true, cancellationToken);

        return FindContract(
            expiryDate,
            strike,
            normalizedType);
    }

    public async Task<KotakOptionInstrument> ResolveAsync(
        DateOnly expiryDate,
        decimal strike,
        string optionType,
        CancellationToken cancellationToken)
    {
        string normalizedType = NormalizeOptionType(optionType);

        KotakOptionInstrument? instrument = await TryResolveAsync(
            expiryDate,
            strike,
            normalizedType,
            cancellationToken);

        if (instrument != null)
        {
            return instrument;
        }

        List<KotakOptionInstrument> sameExpiry = _instruments
            .Where(x => x.ExpiryDate == expiryDate)
            .OrderBy(x => Math.Abs(x.Strike - strike))
            .ThenBy(x => x.Strike)
            .ToList();

        List<KotakOptionInstrument> sameContractFamily = sameExpiry
            .Where(x => x.OptionType.Equals(
                normalizedType,
                StringComparison.OrdinalIgnoreCase))
            .ToList();

        string suggestionText = BuildSuggestionText(
            strike,
            normalizedType,
            sameExpiry,
            sameContractFamily);

        throw new InvalidOperationException(
            $"Kotak instrument not found for NIFTY {expiryDate:yyyy-MM-dd} " +
            $"{strike:0.##} {normalizedType}. " +
            suggestionText);
    }

    private async Task<KotakOptionInstrument?> TryResolveFromOptionChainAsync(
        DateOnly expiryDate,
        decimal strike,
        string normalizedType,
        CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument chain = await _client.GetNiftyOptionChainAsync(
                expiryDate,
                cancellationToken);

            JsonElement root = chain.RootElement;

            if (!root.TryGetProperty("data", out JsonElement data))
            {
                return null;
            }

            int lotSize = 0;

            if (data.TryGetProperty("common_data", out JsonElement commonData))
            {
                string commonLot = JsonString(commonData, "mktLot");
                int.TryParse(
                    commonLot,
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out lotSize);
            }

            string arrayName = normalizedType == "CE" ? "call" : "put";

            if (!data.TryGetProperty(arrayName, out JsonElement contracts) ||
                contracts.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (JsonElement item in contracts.EnumerateArray())
            {
                if (!item.TryGetProperty("instrument", out JsonElement instrument))
                {
                    continue;
                }

                string strikeText = JsonString(instrument, "strikePrice");

                if (!decimal.TryParse(
                        strikeText,
                        NumberStyles.Any,
                        CultureInfo.InvariantCulture,
                        out decimal parsedStrike) ||
                    parsedStrike != strike)
                {
                    continue;
                }

                string optionType = NormalizeOptionType(
                    JsonString(instrument, "optionType"));

                if (!optionType.Equals(
                        normalizedType,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string neoSymbol = JsonString(instrument, "neoSymbol");
                string tradingSymbol = JsonString(instrument, "symbol");

                (string exchange, string token) = ParseNeoSymbol(neoSymbol);

                if (string.IsNullOrWhiteSpace(token) ||
                    string.IsNullOrWhiteSpace(tradingSymbol))
                {
                    continue;
                }

                if (lotSize <= 0) return null; // Resolve from scrip master; never invent a broker lot size.
                return new KotakOptionInstrument
                {
                    InstrumentToken = token,
                    TradingSymbol = tradingSymbol,
                    ExchangeSegment = string.IsNullOrWhiteSpace(exchange)
                        ? _options.ExchangeSegment
                        : exchange,
                    ExpiryDate = expiryDate,
                    Strike = parsedStrike,
                    OptionType = normalizedType,
                    LotSize = lotSize
                };
            }
        }
        catch
        {
            // The scrip-master fallback below remains available if the
            // market-data option-chain endpoint is temporarily unavailable.
        }

        return null;
    }

    private void CacheInstrument(KotakOptionInstrument instrument)
    {
        KotakOptionInstrument? existing = _instruments.FirstOrDefault(x =>
            x.InstrumentToken.Equals(
                instrument.InstrumentToken,
                StringComparison.OrdinalIgnoreCase));

        if (existing == null)
        {
            _instruments.Add(instrument);
        }
    }

    private static (string Exchange, string Token) ParseNeoSymbol(string neoSymbol)
    {
        string[] parts = neoSymbol.Split(
            '|',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length != 2)
        {
            return (string.Empty, string.Empty);
        }

        return (parts[0], parts[1]);
    }

    private static string JsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return string.Empty;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : property.ToString();
    }

    private KotakOptionInstrument? FindByTokenOrSymbol(
        string instrumentToken,
        string tradingSymbol)
    {
        if (!string.IsNullOrWhiteSpace(instrumentToken))
        {
            KotakOptionInstrument? byToken = _instruments.FirstOrDefault(x =>
                x.InstrumentToken.Equals(
                    instrumentToken.Trim(),
                    StringComparison.OrdinalIgnoreCase));

            if (byToken != null)
            {
                return byToken;
            }
        }

        if (!string.IsNullOrWhiteSpace(tradingSymbol))
        {
            string normalizedSymbol = tradingSymbol.Trim();

            KotakOptionInstrument? bySymbol = _instruments.FirstOrDefault(x =>
                x.TradingSymbol.Equals(
                    normalizedSymbol,
                    StringComparison.OrdinalIgnoreCase));

            if (bySymbol != null)
            {
                return bySymbol;
            }
        }

        return null;
    }

    private KotakOptionInstrument? FindContract(
        DateOnly expiryDate,
        decimal strike,
        string normalizedType)
    {
        return _instruments.FirstOrDefault(x =>
            x.ExpiryDate == expiryDate &&
            x.Strike == strike &&
            x.OptionType.Equals(
                normalizedType,
                StringComparison.OrdinalIgnoreCase));
    }

    private string BuildSuggestionText(
        decimal strike,
        string normalizedType,
        IReadOnlyList<KotakOptionInstrument> sameExpiry,
        IReadOnlyList<KotakOptionInstrument> sameContractFamily)
    {
        if (sameContractFamily.Count > 0)
        {
            KotakOptionInstrument? tenTimesSuggestion = null;

            if (strike > 0m && strike < 10000m)
            {
                decimal scaledStrike = strike * 10m;

                tenTimesSuggestion = sameContractFamily.FirstOrDefault(x =>
                    x.Strike == scaledStrike);
            }

            if (tenTimesSuggestion != null)
            {
                return
                    $"Did you mean {tenTimesSuggestion.Strike:0.##} " +
                    $"{tenTimesSuggestion.OptionType}? " +
                    $"Kotak lot size is {tenTimesSuggestion.LotSize}.";
            }

            string nearby = string.Join(
                ", ",
                sameContractFamily
                    .Take(8)
                    .Select(x => x.Strike.ToString(
                        "0.##",
                        CultureInfo.InvariantCulture)));

            int brokerLotSize = sameContractFamily
                .Select(x => x.LotSize)
                .FirstOrDefault(x => x > 0);

            return
                $"Nearby valid {normalizedType} strikes for that expiry: {nearby}." +
                (brokerLotSize > 0
                    ? $" Kotak lot size is {brokerLotSize}."
                    : string.Empty);
        }

        if (sameExpiry.Count > 0)
        {
            string availableTypes = string.Join(
                ", ",
                sameExpiry
                    .Select(x => x.OptionType)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x));

            return
                $"The expiry exists in Kotak master, but no {normalizedType} rows were parsed. " +
                $"Parsed option types for the expiry: {availableTypes}.";
        }

        string availableExpiries = string.Join(
            ", ",
            _instruments
                .Select(x => x.ExpiryDate)
                .Distinct()
                .OrderBy(x => x)
                .Take(12)
                .Select(x => x.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));

        if (!string.IsNullOrWhiteSpace(availableExpiries))
        {
            return
                "No contracts were parsed for that expiry. " +
                $"Kotak master currently parsed these NIFTY expiries: {availableExpiries}.";
        }

        return
            "No NIFTY option contracts could be parsed from Kotak's NSE F&O scrip master.";
    }

    private async Task EnsureMasterLoadedAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        DateOnly today = MarketClock.IndiaDateNow();

        if (!forceRefresh &&
            _loadedDate == today &&
            _instruments.Count > 0)
        {
            return;
        }

        await _masterGate.WaitAsync(cancellationToken);

        try
        {
            if (!forceRefresh &&
                _loadedDate == today &&
                _instruments.Count > 0)
            {
                return;
            }

            await _authenticationService.EnsureAuthenticatedAsync(
                cancellationToken);

            string directory = Path.Combine(
                AppContext.BaseDirectory,
                "data",
                "kotak-scrip-master");

            Directory.CreateDirectory(directory);

            string path = Path.Combine(
                directory,
                $"nse_fo-{today:yyyyMMdd}.csv");

            if (forceRefresh || !File.Exists(path))
            {
                await _client.DownloadNseFoScripMasterAsync(
                    path,
                    cancellationToken);
            }

            List<KotakOptionInstrument> parsed = await ParseAsync(
                path,
                cancellationToken);

            if (parsed.Count == 0 && !forceRefresh)
            {
                await _client.DownloadNseFoScripMasterAsync(
                    path,
                    cancellationToken);

                parsed = await ParseAsync(
                    path,
                    cancellationToken);
            }

            _instruments = parsed;
            _loadedDate = today;
        }
        finally
        {
            _masterGate.Release();
        }
    }

    private async Task<List<KotakOptionInstrument>> ParseAsync(
        string path,
        CancellationToken cancellationToken)
    {
        string[] lines = await File.ReadAllLinesAsync(
            path,
            cancellationToken);

        if (lines.Length < 2)
        {
            throw new InvalidOperationException(
                "Kotak NSE F&O scrip master is empty.");
        }

        List<string> headers = ParseCsvLine(lines[0]);
        var result = new List<KotakOptionInstrument>();

        foreach (string line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            List<string> values = ParseCsvLine(line);

            if (values.Count != headers.Count)
            {
                continue;
            }

            var row = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

            for (int index = 0; index < headers.Count; index++)
            {
                row[headers[index].Trim()] = values[index].Trim();
            }

            string exchange = GetValue(row, "pExchSeg");
            string symbolName = GetValue(row, "pSymbolName");
            string assetCode = GetValue(row, "pAssetCode");
            string referenceKey = GetValue(row, "pScripRefKey");
            string tradingSymbol = GetValue(row, "pTrdSymbol");

            if (!exchange.Equals(
                    _options.ExchangeSegment,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool isNifty =
                symbolName.Equals("NIFTY", StringComparison.OrdinalIgnoreCase) ||
                assetCode.Equals("NIFTY", StringComparison.OrdinalIgnoreCase) ||
                referenceKey.StartsWith("NIFTY", StringComparison.OrdinalIgnoreCase) ||
                tradingSymbol.StartsWith("NIFTY", StringComparison.OrdinalIgnoreCase);

            if (!isNifty)
            {
                continue;
            }

            NiftyContractIdentity? symbolIdentity = ParseNiftyContractIdentity(
                referenceKey,
                tradingSymbol);

            string optionType = NormalizeOptionType(
                GetValue(row, "pOptionType"));

            if (string.IsNullOrWhiteSpace(optionType) && symbolIdentity != null)
            {
                optionType = symbolIdentity.OptionType;
            }

            if (optionType != "CE" && optionType != "PE")
            {
                continue;
            }

            DateOnly? expiryFromFields = ParseExpiry(
                GetValue(row, "pExpiryDate"),
                GetValue(row, "lExpiryDate"));

            DateOnly? expiry = symbolIdentity?.ExpiryDate;

            if (!expiry.HasValue)
            {
                expiry = NormalizeExpiryYearFromTradingSymbol(
                    expiryFromFields,
                    tradingSymbol);
            }

            decimal? strike = symbolIdentity?.Strike;

            if (!strike.HasValue)
            {
                strike = ParseStrike(
                    GetValue(row, "dStrikePrice;"),
                    GetValue(row, "dStrikePrice"));
            }

            int lotSize = ParseFirstPositiveInt(
                GetValue(row, "lLotSize"),
                GetValue(row, "iLotSize"),
                GetValue(row, "iBoardLotQty"));

            string token = GetValue(row, "pSymbol");

            if (!expiry.HasValue ||
                !strike.HasValue ||
                strike.Value <= 0m ||
                lotSize <= 0 ||
                string.IsNullOrWhiteSpace(token) ||
                string.IsNullOrWhiteSpace(tradingSymbol))
            {
                continue;
            }

            result.Add(
                new KotakOptionInstrument
                {
                    InstrumentToken = token,
                    TradingSymbol = tradingSymbol,
                    ExchangeSegment = exchange,
                    ExpiryDate = expiry.Value,
                    Strike = strike.Value,
                    OptionType = optionType,
                    LotSize = lotSize
                });
        }

        return result
            .GroupBy(x => new
            {
                x.InstrumentToken,
                x.TradingSymbol
            })
            .Select(group => group.First())
            .ToList();
    }

    private static NiftyContractIdentity? ParseNiftyContractIdentity(
        string referenceKey,
        string tradingSymbol)
    {
        NiftyContractIdentity? fromReferenceKey =
            ParseReferenceKeyIdentity(referenceKey);

        if (fromReferenceKey != null)
        {
            return fromReferenceKey;
        }

        return ParseTradingSymbolIdentity(tradingSymbol);
    }

    private static NiftyContractIdentity? ParseReferenceKeyIdentity(
        string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        Match match = Regex.Match(
            value.Trim().ToUpperInvariant(),
            @"^NIFTY(?<day>\d{2})(?<month>[A-Z]{3})(?<year>\d{2})(?<strike>\d+(?:\.\d+)?)(?<type>CE|PE)$",
            RegexOptions.CultureInvariant);

        if (!match.Success)
        {
            return null;
        }

        if (!TryBuildDate(
                match.Groups["year"].Value,
                match.Groups["month"].Value,
                match.Groups["day"].Value,
                out DateOnly expiryDate))
        {
            return null;
        }

        if (!decimal.TryParse(
                match.Groups["strike"].Value,
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out decimal strike))
        {
            return null;
        }

        return new NiftyContractIdentity(
            expiryDate,
            strike,
            match.Groups["type"].Value);
    }

    private static NiftyContractIdentity? ParseTradingSymbolIdentity(
        string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string symbol = value.Trim().ToUpperInvariant();

        // Current weekly index-option format, for example:
        // NIFTY26O0622600CE = 06-Oct-2026, 22600 CE.
        Match weekly = Regex.Match(
            symbol,
            @"^NIFTY(?<year>\d{2})(?<month>[1-9OND])(?<day>\d{2})(?<strike>\d+)(?<type>CE|PE)$",
            RegexOptions.CultureInvariant);

        if (weekly.Success)
        {
            if (!TryBuildDateFromWeeklyCode(
                    weekly.Groups["year"].Value,
                    weekly.Groups["month"].Value,
                    weekly.Groups["day"].Value,
                    out DateOnly expiryDate))
            {
                return null;
            }

            if (!decimal.TryParse(
                    weekly.Groups["strike"].Value,
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out decimal strike))
            {
                return null;
            }

            return new NiftyContractIdentity(
                expiryDate,
                strike,
                weekly.Groups["type"].Value);
        }

        return null;
    }

    private static DateOnly? NormalizeExpiryYearFromTradingSymbol(
        DateOnly? parsedExpiry,
        string tradingSymbol)
    {
        if (!parsedExpiry.HasValue || string.IsNullOrWhiteSpace(tradingSymbol))
        {
            return parsedExpiry;
        }

        Match yearMatch = Regex.Match(
            tradingSymbol.Trim().ToUpperInvariant(),
            @"^NIFTY(?<year>\d{2})",
            RegexOptions.CultureInvariant);

        if (!yearMatch.Success ||
            !int.TryParse(
                yearMatch.Groups["year"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int shortYear))
        {
            return parsedExpiry;
        }

        int expectedYear = 2000 + shortYear;

        if (parsedExpiry.Value.Year == expectedYear)
        {
            return parsedExpiry;
        }

        try
        {
            return new DateOnly(
                expectedYear,
                parsedExpiry.Value.Month,
                parsedExpiry.Value.Day);
        }
        catch
        {
            return parsedExpiry;
        }
    }

    private static bool TryBuildDate(
        string shortYear,
        string monthText,
        string dayText,
        out DateOnly result)
    {
        result = default;

        if (!int.TryParse(
                shortYear,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int year) ||
            !int.TryParse(
                dayText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int day))
        {
            return false;
        }

        if (!DateTime.TryParseExact(
                monthText,
                "MMM",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime monthDate))
        {
            return false;
        }

        try
        {
            result = new DateOnly(
                2000 + year,
                monthDate.Month,
                day);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryBuildDateFromWeeklyCode(
        string shortYear,
        string monthCode,
        string dayText,
        out DateOnly result)
    {
        result = default;

        if (!int.TryParse(
                shortYear,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int year) ||
            !int.TryParse(
                dayText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int day))
        {
            return false;
        }

        int month = monthCode switch
        {
            "1" => 1,
            "2" => 2,
            "3" => 3,
            "4" => 4,
            "5" => 5,
            "6" => 6,
            "7" => 7,
            "8" => 8,
            "9" => 9,
            "O" => 10,
            "N" => 11,
            "D" => 12,
            _ => 0
        };

        if (month == 0)
        {
            return false;
        }

        try
        {
            result = new DateOnly(
                2000 + year,
                month,
                day);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeOptionType(string value)
    {
        string normalized = value
            .Trim()
            .ToUpperInvariant();

        return normalized switch
        {
            "CALL" => "CE",
            "C" => "CE",
            "CE" => "CE",
            "PUT" => "PE",
            "P" => "PE",
            "PE" => "PE",
            _ => normalized
        };
    }

    private static DateOnly? ParseExpiry(params string[] values)
    {
        string[] formats =
        [
            "ddMMMyyyy",
            "dd-MMM-yyyy",
            "dd/MM/yyyy",
            "yyyy-MM-dd",
            "ddMMyy",
            "yyyyMMdd"
        ];

        foreach (string rawValue in values)
        {
            string value = rawValue.Trim();

            if (string.IsNullOrWhiteSpace(value) || value == "-1")
            {
                continue;
            }

            // lExpiryDate is sometimes Unix seconds.
            if (long.TryParse(
                    value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out long epoch) &&
                epoch > 1_000_000_000L)
            {
                try
                {
                    DateTime indiaDate = TimeZoneInfo.ConvertTime(
                        DateTimeOffset.FromUnixTimeSeconds(epoch),
                        TimeZoneInfo.FindSystemTimeZoneById(
                            OperatingSystem.IsWindows()
                                ? "India Standard Time"
                                : "Asia/Kolkata"))
                        .DateTime;

                    return DateOnly.FromDateTime(indiaDate);
                }
                catch
                {
                    // Continue with textual parsers below.
                }
            }

            foreach (string format in formats)
            {
                if (DateTime.TryParseExact(
                    value,
                    format,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime parsedExact))
                {
                    return DateOnly.FromDateTime(parsedExact);
                }
            }

            if (DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime parsed))
            {
                return DateOnly.FromDateTime(parsed);
            }
        }

        return null;
    }

    private static decimal? ParseStrike(params string[] values)
    {
        foreach (string rawValue in values)
        {
            if (!decimal.TryParse(
                rawValue.Trim(),
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out decimal strike))
            {
                continue;
            }

            if (strike > 100000m)
            {
                strike /= 100m;
            }

            if (strike >= 0m)
            {
                return strike;
            }
        }

        return null;
    }

    private static int ParseFirstPositiveInt(params string[] values)
    {
        foreach (string value in values)
        {
            if (int.TryParse(
                    value,
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out int parsed) &&
                parsed > 0)
            {
                return parsed;
            }
        }

        return 0;
    }

    private static string GetValue(
        IReadOnlyDictionary<string, string> row,
        string name)
    {
        return row.TryGetValue(name, out string? value)
            ? value
            : string.Empty;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;

        for (int index = 0; index < line.Length; index++)
        {
            char character = line[index];

            if (character == '"')
            {
                if (quoted &&
                    index + 1 < line.Length &&
                    line[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }

                continue;
            }

            if (character == ',' && !quoted)
            {
                result.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(character);
        }

        result.Add(current.ToString());
        return result;
    }

    private sealed record NiftyContractIdentity(
        DateOnly ExpiryDate,
        decimal Strike,
        string OptionType);
}
