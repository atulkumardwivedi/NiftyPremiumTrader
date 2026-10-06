using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NiftyPremiumTrader.Api.Options;

namespace NiftyPremiumTrader.Api.Services;

public sealed class KotakOrderExecutionResult
{
    public string OrderId { get; set; } = string.Empty;
    public decimal FillPrice { get; set; }
    public string TradingSymbol { get; set; } = string.Empty;
    public string InstrumentToken { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool IsFilled { get; set; }
}

public sealed class KotakOrderService
{
    private readonly KotakNeoClient _client;
    private readonly KotakAuthenticationService _authenticationService;
    private readonly KotakInstrumentService _instrumentService;
    private readonly KotakNeoOptions _options;
    private readonly LiveTradingOptions _liveOptions;

    public KotakOrderService(
        KotakNeoClient client,
        KotakAuthenticationService authenticationService,
        KotakInstrumentService instrumentService,
        IOptions<KotakNeoOptions> options,
        IOptions<LiveTradingOptions> liveOptions)
    {
        _client = client;
        _authenticationService = authenticationService;
        _instrumentService = instrumentService;
        _options = options.Value;
        _liveOptions = liveOptions.Value;
    }

    public bool IsAuthenticated => _client.IsAuthenticated;

    public async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (!_liveOptions.AllowBrokerOrders)
        {
            throw new InvalidOperationException(
                "Real broker orders are disabled. Set LiveTrading:AllowBrokerOrders=true after paper testing.");
        }

        await _authenticationService.EnsureAuthenticatedAsync(cancellationToken);
    }

    public async Task<KotakOrderExecutionResult> PlaceOptionMarketOrderAsync(
        DateOnly expiryDate,
        decimal strike,
        string optionType,
        string action,
        int quantity,
        int configuredLotSize,
        string tag,
        CancellationToken cancellationToken)
    {
        return await PlaceOptionOrderAsync(
            expiryDate,
            strike,
            optionType,
            action,
            quantity,
            configuredLotSize,
            "MKT",
            null,
            tag,
            cancellationToken);
    }

    public async Task<KotakOrderExecutionResult> PlaceOptionOrderAsync(
        DateOnly expiryDate,
        decimal strike,
        string optionType,
        string action,
        int quantity,
        int configuredLotSize,
        string orderType,
        decimal? limitPrice,
        string tag,
        CancellationToken cancellationToken)
    {
        await EnsureReadyAsync(cancellationToken);

        KotakOptionInstrument instrument = await _instrumentService.ResolveAsync(
            expiryDate,
            strike,
            optionType,
            cancellationToken);

        if (configuredLotSize != instrument.LotSize)
        {
            throw new InvalidOperationException(
                $"Configured lot size is {configuredLotSize}, but Kotak scrip master reports " +
                $"{instrument.LotSize} for {instrument.TradingSymbol}. " +
                "Change the UI lot size before sending a real order.");
        }

        if (quantity <= 0 || quantity % instrument.LotSize != 0)
        {
            throw new InvalidOperationException(
                $"Quantity {quantity} is not a valid multiple of Kotak lot size {instrument.LotSize}.");
        }

        return await PlaceResolvedOrderAsync(
            instrument.TradingSymbol,
            instrument.InstrumentToken,
            action,
            quantity,
            orderType,
            limitPrice,
            tag,
            cancellationToken);
    }

    public async Task<KotakOrderExecutionResult> PlaceTradingSymbolOrderAsync(
        string tradingSymbol,
        string instrumentToken,
        string action,
        int quantity,
        string orderType,
        decimal? limitPrice,
        string tag,
        CancellationToken cancellationToken)
    {
        await EnsureReadyAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(tradingSymbol))
        {
            throw new ArgumentException("Trading symbol is required.");
        }

        if (quantity <= 0)
        {
            throw new ArgumentException("Quantity must be greater than zero.");
        }

        return await PlaceResolvedOrderAsync(
            tradingSymbol,
            instrumentToken,
            action,
            quantity,
            orderType,
            limitPrice,
            tag,
            cancellationToken);
    }

    private async Task<KotakOrderExecutionResult> PlaceResolvedOrderAsync(
        string tradingSymbol,
        string instrumentToken,
        string action,
        int quantity,
        string orderType,
        decimal? limitPrice,
        string tag,
        CancellationToken cancellationToken)
    {
        string normalizedType = NormalizeOrderType(orderType);
        string transactionType = NormalizeAction(action);

        if (normalizedType == "L" && (!limitPrice.HasValue || limitPrice.Value <= 0m))
        {
            throw new ArgumentException("A positive limit price is required for a LIMIT order.");
        }

        var request = new KotakPlaceOrderRequest
        {
            ExchangeSegment = _options.ExchangeSegment,
            Product = _options.Product,
            OrderType = normalizedType,
            Price = normalizedType == "MKT"
                ? "0"
                : limitPrice!.Value.ToString("0.00", CultureInfo.InvariantCulture),
            Quantity = quantity,
            TradingSymbol = tradingSymbol,
            TransactionType = transactionType,
            Tag = tag
        };

        using JsonDocument response = await _client.PlaceOrderAsync(
            request,
            cancellationToken);

        string? orderId = FindString(
            response.RootElement,
            "nOrdNo",
            "orderId",
            "order_id",
            "orderNo");

        if (string.IsNullOrWhiteSpace(orderId))
        {
            throw new InvalidOperationException(
                "Kotak did not return an order id. Response: " +
                response.RootElement.ToString());
        }

        if (normalizedType == "MKT")
        {
            (decimal fillPrice, string status) = await WaitForFillAsync(
                orderId,
                cancellationToken);

            return new KotakOrderExecutionResult
            {
                OrderId = orderId,
                FillPrice = fillPrice,
                TradingSymbol = tradingSymbol,
                InstrumentToken = instrumentToken,
                Status = status,
                IsFilled = fillPrice > 0m
            };
        }

        (decimal? currentFill, string currentStatus) = await ReadOrderStateAsync(
            orderId,
            cancellationToken);

        return new KotakOrderExecutionResult
        {
            OrderId = orderId,
            FillPrice = currentFill ?? 0m,
            TradingSymbol = tradingSymbol,
            InstrumentToken = instrumentToken,
            Status = string.IsNullOrWhiteSpace(currentStatus)
                ? "PLACED"
                : currentStatus,
            IsFilled = currentFill.HasValue && currentFill.Value > 0m
        };
    }

    private async Task<(decimal FillPrice, string Status)> WaitForFillAsync(
        string orderId,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 12;

        for (int attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            (decimal? price, string status) = await ReadOrderStateAsync(
                orderId,
                cancellationToken);

            if (IsRejected(status))
            {
                throw new InvalidOperationException(
                    $"Kotak order {orderId} failed with status {status}.");
            }

            if (price.HasValue && price.Value > 0m)
            {
                return (price.Value, status);
            }

            if (attempt < maximumAttempts)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(500),
                    cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"Kotak order {orderId} was sent but no confirmed fill price was returned. " +
            "Check the Kotak order book before restarting live trading.");
    }

    private async Task<(decimal? FillPrice, string Status)> ReadOrderStateAsync(
        string orderId,
        CancellationToken cancellationToken)
    {
        using JsonDocument history = await _client.GetOrderHistoryAsync(
            orderId,
            cancellationToken);

        string status = FindString(
            history.RootElement,
            "ordSt",
            "orderStatus",
            "status") ?? string.Empty;

        decimal? price = FindDecimal(
            history.RootElement,
            "avgPrc",
            "averagePrice",
            "fillPrice",
            "flPrc");

        return (price, status);
    }

    private static string NormalizeAction(string action)
    {
        string normalized = action.Trim().ToUpperInvariant();

        return normalized switch
        {
            "SELL" => "S",
            "S" => "S",
            "BUY" => "B",
            "B" => "B",
            _ => throw new ArgumentException("Action must be BUY or SELL.")
        };
    }

    private static string NormalizeOrderType(string orderType)
    {
        string normalized = orderType.Trim().ToUpperInvariant();

        return normalized switch
        {
            "MARKET" => "MKT",
            "MKT" => "MKT",
            "LIMIT" => "L",
            "L" => "L",
            _ => throw new ArgumentException("Order type must be MARKET or LIMIT.")
        };
    }

    private static bool IsRejected(string status)
    {
        string value = status.ToUpperInvariant();

        return value.Contains("REJECT") ||
            value.Contains("CANCEL");
    }

    private static string? FindString(
        JsonElement element,
        params string[] names)
    {
        foreach (string name in names)
        {
            string? value = FindString(element, name);

            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? FindString(
        JsonElement element,
        string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value.ToString();
                }

                string? nested = FindString(property.Value, name);

                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                string? nested = FindString(item, name);

                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static decimal? FindDecimal(
        JsonElement element,
        params string[] names)
    {
        foreach (string name in names)
        {
            decimal? value = FindDecimal(element, name);

            if (value.HasValue)
            {
                return value;
            }
        }

        return null;
    }

    private static decimal? FindDecimal(
        JsonElement element,
        string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value.ValueKind == JsonValueKind.Number &&
                        property.Value.TryGetDecimal(out decimal number))
                    {
                        return number;
                    }

                    if (decimal.TryParse(
                        property.Value.ToString(),
                        NumberStyles.Any,
                        CultureInfo.InvariantCulture,
                        out decimal parsed))
                    {
                        return parsed;
                    }
                }

                decimal? nested = FindDecimal(property.Value, name);

                if (nested.HasValue)
                {
                    return nested;
                }
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                decimal? nested = FindDecimal(item, name);

                if (nested.HasValue)
                {
                    return nested;
                }
            }
        }

        return null;
    }
}
