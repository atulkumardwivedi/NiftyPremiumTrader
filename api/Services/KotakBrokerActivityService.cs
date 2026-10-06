using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NiftyPremiumTrader.Api.Data;
using NiftyPremiumTrader.Api.Models;
using NiftyPremiumTrader.Api.Options;

namespace NiftyPremiumTrader.Api.Services;

public sealed class KotakBrokerActivityService
{
    private readonly KotakNeoClient _client;
    private readonly KotakAuthenticationService _authenticationService;
    private readonly KotakOrderService _orderService;
    private readonly KotakInstrumentService _instrumentService;
    private readonly LiveTradingService _liveTradingService;
    private readonly BrokerActivityRepository _repository;
    private readonly KotakNeoOptions _options;

    public KotakBrokerActivityService(
        KotakNeoClient client,
        KotakAuthenticationService authenticationService,
        KotakOrderService orderService,
        KotakInstrumentService instrumentService,
        LiveTradingService liveTradingService,
        BrokerActivityRepository repository,
        IOptions<KotakNeoOptions> options)
    {
        _client = client;
        _authenticationService = authenticationService;
        _orderService = orderService;
        _instrumentService = instrumentService;
        _liveTradingService = liveTradingService;
        _repository = repository;
        _options = options.Value;
    }

    public async Task<BrokerDashboardResponse> GetDashboardAsync(
        CancellationToken cancellationToken)
    {
        await _authenticationService.EnsureAuthenticatedAsync(cancellationToken);

        // Open positions are the primary broker state for this screen. Do not
        // make them disappear just because the order-book endpoint temporarily
        // returns a 400/"body invalid" response.
        using JsonDocument positionsJson = await _client.GetPositionsAsync(cancellationToken);

        List<BrokerPositionView> positions = ParsePositions(positionsJson.RootElement)
            .Where(IsNiftyFno)
            .Where(x => x.NetQuantity != 0)
            .ToList();

        List<BrokerOrderView> orders = [];
        string brokerMessage =
            "Kotak open positions refreshed. Completed trade history is intentionally hidden from the live screen.";

        try
        {
            using JsonDocument orderBook = await _client.GetOrderBookAsync(cancellationToken);
            orders = ParseOrders(orderBook.RootElement);
        }
        catch (InvalidOperationException ex)
        {
            brokerMessage += " Working-order refresh unavailable: " + ex.Message;
        }

        DateOnly today = MarketClock.IndiaDateNow();

        HashSet<string> systemOrderIds = await _repository.GetSystemBrokerOrderIdsAsync(
            today,
            cancellationToken);

        HashSet<string> manualUiOrderIds = await _repository.GetManualUiBrokerOrderIdsAsync(
            today,
            cancellationToken);

        foreach (BrokerOrderView order in orders)
        {
            order.Source = ClassifyOrder(
                order.OrderId,
                order.Tag,
                systemOrderIds,
                manualUiOrderIds);
        }

        foreach (BrokerPositionView position in positions)
        {
            HashSet<string> sources = orders
                .Where(x => SameInstrument(x.TradingSymbol, position.TradingSymbol))
                .Select(x => x.Source)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            position.Source = sources.Count switch
            {
                0 => "MANUAL_APP",
                1 => sources.First(),
                _ => "MIXED"
            };

            KotakOptionInstrument? instrument =
                await _instrumentService.TryResolveByTokenOrSymbolAsync(
                    position.InstrumentToken,
                    position.TradingSymbol,
                    cancellationToken);

            if (instrument != null)
            {
                position.InstrumentToken = instrument.InstrumentToken;
                position.ExpiryDate = instrument.ExpiryDate;
                position.Strike = instrument.Strike;
                position.OptionType = instrument.OptionType;
                position.LotSize = instrument.LotSize;
            }

            position.PositionRole = BuildPositionRole(position);

            // Every OPEN broker row can be exited from this screen. For a
            // SYSTEM/MIXED row the live strategy is stopped immediately after
            // the exit order is accepted, preventing a duplicate automatic exit.
            position.CanExitFromUi = true;
            position.ExitRestriction = string.Empty;
        }

        return new BrokerDashboardResponse
        {
            RefreshedAt = MarketClock.ToIndia(DateTimeOffset.UtcNow),
            Positions = positions
                .OrderBy(x => x.TradingSymbol)
                .ToList(),
            Orders = orders
                .Where(IsNiftyFno)
                .Where(x => !IsFinalOrderStatus(x.Status))
                .ToList(),
            Trades = [],
            Message = brokerMessage
        };
    }

    public async Task<KotakOrderExecutionResult> PlaceManualOrderAsync(
        ManualBrokerOrderRequest request,
        CancellationToken cancellationToken)
    {
        ValidateManualRequest(request);

        KotakOptionInstrument instrument = await _instrumentService.ResolveAsync(
            request.ExpiryDate,
            request.Strike,
            request.OptionType,
            cancellationToken);

        int actualLotSize = instrument.LotSize;
        int quantity = checked(request.Lots * actualLotSize);

        KotakOrderExecutionResult result = await _orderService.PlaceOptionOrderAsync(
            request.ExpiryDate,
            request.Strike,
            request.OptionType,
            request.Side,
            quantity,
            actualLotSize,
            NormalizeOrderType(request.OrderType),
            request.LimitPrice,
            "NPT:MANUAL:UI",
            cancellationToken);

        await _repository.SaveManualOrderAsync(
            new ManualBrokerOrderRecord
            {
                LocalOrderId = Guid.NewGuid(),
                BrokerOrderId = result.OrderId,
                SessionDate = MarketClock.IndiaDateNow(),
                TradingSymbol = result.TradingSymbol,
                InstrumentToken = result.InstrumentToken,
                Strike = request.Strike,
                OptionType = request.OptionType.ToUpperInvariant(),
                Side = request.Side.ToUpperInvariant(),
                Quantity = quantity,
                Lots = request.Lots,
                LotSize = actualLotSize,
                OrderType = NormalizeOrderType(request.OrderType),
                LimitPrice = request.LimitPrice,
                Purpose = "ENTRY",
                Status = result.Status,
                AveragePrice = result.FillPrice > 0 ? result.FillPrice : null
            },
            cancellationToken);

        return result;
    }

    public async Task<KotakOrderExecutionResult> ExitManualPositionAsync(
        ExitBrokerPositionRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.TradingSymbol))
        {
            throw new ArgumentException("Trading symbol is required.");
        }

        BrokerDashboardResponse dashboard = await GetDashboardAsync(cancellationToken);

        BrokerPositionView? position = dashboard.Positions.FirstOrDefault(x =>
            x.TradingSymbol.Equals(
                request.TradingSymbol,
                StringComparison.OrdinalIgnoreCase));

        if (position == null || position.NetQuantity == 0)
        {
            throw new InvalidOperationException(
                $"No open Kotak position exists for {request.TradingSymbol}.");
        }

        string action = position.NetQuantity < 0 ? "BUY" : "SELL";
        int quantity = Math.Abs(position.NetQuantity);
        string orderType = NormalizeOrderType(request.OrderType);

        KotakOrderExecutionResult result = await _orderService.PlaceTradingSymbolOrderAsync(
            position.TradingSymbol,
            position.InstrumentToken,
            action,
            quantity,
            orderType,
            request.LimitPrice,
            "NPT:EXIT:UI",
            cancellationToken);

        int lotSize = Math.Max(1, position.LotSize);
        int lots = quantity % lotSize == 0
            ? quantity / lotSize
            : 0;

        await _repository.SaveManualOrderAsync(
            new ManualBrokerOrderRecord
            {
                LocalOrderId = Guid.NewGuid(),
                BrokerOrderId = result.OrderId,
                SessionDate = MarketClock.IndiaDateNow(),
                TradingSymbol = position.TradingSymbol,
                InstrumentToken = position.InstrumentToken,
                Side = action,
                Quantity = quantity,
                Lots = lots,
                LotSize = lotSize,
                OrderType = orderType,
                LimitPrice = request.LimitPrice,
                Purpose = "EXIT",
                Status = result.Status,
                AveragePrice = result.FillPrice > 0 ? result.FillPrice : null
            },
            cancellationToken);

        _liveTradingService.NotifyManualBrokerPositionExit(
            position.TradingSymbol,
            position.Source);

        return result;
    }

    private static void ValidateManualRequest(ManualBrokerOrderRequest request)
    {
        if (request.ExpiryDate == default)
        {
            throw new ArgumentException("Expiry date is required.");
        }

        if (request.Strike <= 0m)
        {
            throw new ArgumentException("Strike must be greater than zero.");
        }

        string optionType = request.OptionType.ToUpperInvariant();

        if (optionType != "CE" && optionType != "PE")
        {
            throw new ArgumentException("Option type must be CE or PE.");
        }

        string side = request.Side.ToUpperInvariant();

        if (side != "BUY" && side != "SELL")
        {
            throw new ArgumentException("Side must be BUY or SELL.");
        }

        if (request.Lots <= 0 || request.LotSize <= 0)
        {
            throw new ArgumentException("Lots and lot size must be greater than zero.");
        }

        string orderType = NormalizeOrderType(request.OrderType);

        if (orderType == "L" && (!request.LimitPrice.HasValue || request.LimitPrice.Value <= 0m))
        {
            throw new ArgumentException("Limit price is required for a LIMIT order.");
        }
    }

    private static string NormalizeSide(string value)
    {
        string normalized = value.Trim().ToUpperInvariant();

        return normalized switch
        {
            "B" => "BUY",
            "BUY" => "BUY",
            "S" => "SELL",
            "SELL" => "SELL",
            _ => normalized
        };
    }

    private static string NormalizeOrderType(string value)
    {
        string normalized = value.Trim().ToUpperInvariant();

        return normalized switch
        {
            "MARKET" => "MKT",
            "MKT" => "MKT",
            "LIMIT" => "L",
            "L" => "L",
            _ => throw new ArgumentException("Order type must be MARKET or LIMIT.")
        };
    }

    private static string ClassifyOrder(
        string orderId,
        string tag,
        HashSet<string> systemOrderIds,
        HashSet<string> manualUiOrderIds)
    {
        if (systemOrderIds.Contains(orderId) ||
            tag.StartsWith("NPT:S:", StringComparison.OrdinalIgnoreCase) ||
            tag.StartsWith("NPT:B:", StringComparison.OrdinalIgnoreCase))
        {
            return "SYSTEM";
        }

        if (manualUiOrderIds.Contains(orderId) ||
            tag.StartsWith("NPT:MANUAL:UI", StringComparison.OrdinalIgnoreCase) ||
            tag.StartsWith("NPT:EXIT:UI", StringComparison.OrdinalIgnoreCase))
        {
            return "MANUAL_UI";
        }

        return "MANUAL_APP";
    }

    private static string BuildPositionRole(BrokerPositionView position)
    {
        if (position.Source.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase))
        {
            return position.NetQuantity < 0
                ? "STRATEGY SHORT"
                : "STRATEGY HEDGE";
        }

        if (position.Source.Equals("MIXED", StringComparison.OrdinalIgnoreCase))
        {
            return "MIXED";
        }

        return position.NetQuantity < 0
            ? "MANUAL SHORT"
            : "MANUAL LONG";
    }

    private static bool IsFinalOrderStatus(string status)
    {
        string value = status.Trim().ToUpperInvariant();

        return value.Contains("COMPLETE") ||
               value.Contains("FILLED") ||
               value.Contains("REJECT") ||
               value.Contains("CANCEL");
    }

    private static bool SameInstrument(string left, string right)
    {
        return left.Equals(right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNiftyFno(BrokerPositionView value)
    {
        return value.ExchangeSegment.Equals(
            "nse_fo",
            StringComparison.OrdinalIgnoreCase) &&
            value.TradingSymbol.Contains(
                "NIFTY",
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNiftyFno(BrokerOrderView value)
    {
        return value.ExchangeSegment.Equals(
            "nse_fo",
            StringComparison.OrdinalIgnoreCase) &&
            value.TradingSymbol.Contains(
                "NIFTY",
                StringComparison.OrdinalIgnoreCase);
    }

    private static List<JsonElement> EnumerateObjects(JsonElement root)
    {
        var result = new List<JsonElement>();
        Visit(root, result);
        return result;
    }

    private static void Visit(JsonElement element, List<JsonElement> result)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            result.Add(element);

            foreach (JsonProperty property in element.EnumerateObject())
            {
                Visit(property.Value, result);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                Visit(item, result);
            }
        }
    }

    private static List<BrokerPositionView> ParsePositions(JsonElement root)
    {
        var result = new List<BrokerPositionView>();

        foreach (JsonElement item in EnumerateObjects(root))
        {
            string symbol = StringValue(item, "trdSym", "tradingSymbol", "display_symbol");
            string exchange = StringValue(item, "exSeg", "exchangeSegment", "exchange");

            if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(exchange))
            {
                continue;
            }

            int buyQuantity = IntValue(item, "cfBuyQty") + IntValue(item, "flBuyQty");
            int sellQuantity = IntValue(item, "cfSellQty") + IntValue(item, "flSellQty");
            int directQuantity = IntValue(item, "netQty", "netqty", "qty");
            int netQuantity = directQuantity != 0
                ? directQuantity
                : buyQuantity - sellQuantity;

            if (netQuantity == 0 && buyQuantity == 0 && sellQuantity == 0)
            {
                continue;
            }

            decimal totalBuyAmount =
                DecimalValue(item, "cfBuyAmt") +
                DecimalValue(item, "buyAmt", "flBuyAmt");

            decimal totalSellAmount =
                DecimalValue(item, "cfSellAmt") +
                DecimalValue(item, "sellAmt", "flSellAmt");

            decimal pnlFactor = PositionPriceFactor(item);
            decimal lastPrice = DecimalValue(item, "ltp", "LTP", "lastPrice");
            decimal averagePrice = PositionAveragePrice(
                item,
                buyQuantity,
                sellQuantity,
                totalBuyAmount,
                totalSellAmount,
                pnlFactor);

            decimal brokerPnl = DecimalValue(item, "pnl", "Pnl", "profitLoss");

            if (brokerPnl == 0m && lastPrice > 0m)
            {
                brokerPnl =
                    (totalSellAmount - totalBuyAmount) +
                    (netQuantity * lastPrice * pnlFactor);
            }

            result.Add(new BrokerPositionView
            {
                ExchangeSegment = exchange,
                InstrumentToken = StringValue(item, "tok", "instrumentToken", "exchange_token"),
                TradingSymbol = symbol,
                Product = StringValue(item, "prod", "product"),
                NetQuantity = netQuantity,
                BuyQuantity = buyQuantity,
                SellQuantity = sellQuantity,
                AveragePrice = averagePrice,
                LastPrice = lastPrice,
                Pnl = brokerPnl,
                TotalBuyAmount = totalBuyAmount,
                TotalSellAmount = totalSellAmount,
                PnlFactor = pnlFactor,
                LotSize = Math.Max(1, IntValue(item, "lotSz", "lotSize"))
            });
        }

        return result
            .GroupBy(
                x => string.IsNullOrWhiteSpace(x.InstrumentToken)
                    ? x.TradingSymbol
                    : x.InstrumentToken,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(x => Math.Abs(x.NetQuantity))
                .First())
            .ToList();
    }

    private static decimal PositionPriceFactor(JsonElement item)
    {
        decimal multiplier = PositiveOrOne(DecimalValue(item, "multiplier", "mul"));
        decimal genNum = PositiveOrOne(DecimalValue(item, "genNum"));
        decimal genDen = PositiveOrOne(DecimalValue(item, "genDen"));
        decimal priceNum = PositiveOrOne(DecimalValue(item, "prcNum"));
        decimal priceDen = PositiveOrOne(DecimalValue(item, "prcDen"));

        return multiplier *
               (genNum / genDen) *
               (priceNum / priceDen);
    }

    private static decimal PositionAveragePrice(
        JsonElement item,
        int buyQuantity,
        int sellQuantity,
        decimal totalBuyAmount,
        decimal totalSellAmount,
        decimal factor)
    {
        decimal direct = DecimalValue(item, "avgPrc", "averagePrice");

        if (direct > 0m)
        {
            return direct;
        }

        if (factor <= 0m)
        {
            factor = 1m;
        }

        int precision = IntValue(item, "precision");

        if (precision <= 0)
        {
            precision = 2;
        }

        precision = Math.Clamp(precision, 0, 6);

        if (buyQuantity > sellQuantity && buyQuantity > 0 && totalBuyAmount > 0m)
        {
            return Math.Round(
                totalBuyAmount / (buyQuantity * factor),
                precision,
                MidpointRounding.AwayFromZero);
        }

        if (sellQuantity > buyQuantity && sellQuantity > 0 && totalSellAmount > 0m)
        {
            return Math.Round(
                totalSellAmount / (sellQuantity * factor),
                precision,
                MidpointRounding.AwayFromZero);
        }

        return 0m;
    }

    private static decimal PositiveOrOne(decimal value)
    {
        return value > 0m ? value : 1m;
    }

    private static List<BrokerOrderView> ParseOrders(JsonElement root)
    {
        var result = new List<BrokerOrderView>();

        foreach (JsonElement item in EnumerateObjects(root))
        {
            string orderId = StringValue(item, "nOrdNo", "orderId", "order_id", "orderNo");
            string symbol = StringValue(item, "trdSym", "tradingSymbol", "display_symbol");

            if (string.IsNullOrWhiteSpace(orderId) || string.IsNullOrWhiteSpace(symbol))
            {
                continue;
            }

            result.Add(new BrokerOrderView
            {
                OrderId = orderId,
                InstrumentToken = StringValue(item, "tok", "instrumentToken"),
                TradingSymbol = symbol,
                ExchangeSegment = StringValue(item, "exSeg", "exchangeSegment", "exchange"),
                Side = NormalizeSide(StringValue(item, "trnsTp", "transactionType")),
                OrderType = StringValue(item, "prcTp", "ordType", "orderType", "pt"),
                Status = StringValue(item, "ordSt", "stat", "status"),
                Quantity = IntValue(item, "qty", "quantity"),
                FilledQuantity = IntValue(item, "fldQty", "filledQuantity", "filledQty"),
                AveragePrice = DecimalValue(item, "avgPrc", "averagePrice"),
                Price = DecimalValue(item, "prc", "price"),
                Product = StringValue(item, "prod", "product"),
                Tag = StringValue(item, "remarks", "tag", "GuiOrdId", "ig")
            });
        }

        return result
            .GroupBy(x => x.OrderId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static string StringValue(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (!TryFindProperty(element, name, out JsonElement value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String ||
                value.ValueKind == JsonValueKind.Number)
            {
                return value.ToString();
            }
        }

        return string.Empty;
    }

    private static int IntValue(JsonElement element, params string[] names)
    {
        decimal value = DecimalValue(element, names);
        return decimal.ToInt32(decimal.Truncate(value));
    }

    private static decimal DecimalValue(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (!TryFindProperty(element, name, out JsonElement value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number &&
                value.TryGetDecimal(out decimal number))
            {
                return number;
            }

            if (decimal.TryParse(
                value.ToString(),
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out decimal parsed))
            {
                return parsed;
            }
        }

        return 0m;
    }

    private static bool TryFindProperty(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
