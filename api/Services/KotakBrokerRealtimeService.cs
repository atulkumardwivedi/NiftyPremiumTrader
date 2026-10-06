using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using NiftyPremiumTrader.Api.Models;

namespace NiftyPremiumTrader.Api.Services;

public sealed class KotakBrokerRealtimeService : BackgroundService
{
    private static readonly TimeSpan QuoteInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WorkingOrderSnapshotInterval = TimeSpan.FromSeconds(2);

    private readonly KotakAuthenticationService _authenticationService;
    private readonly KotakNeoClient _client;
    private readonly KotakBrokerActivityService _brokerActivityService;
    private readonly IHubContext<BrokerRealtimeHub> _hub;
    private readonly ILogger<KotakBrokerRealtimeService> _logger;

    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly Dictionary<string, BrokerPositionView> _positionsByToken =
        new(StringComparer.OrdinalIgnoreCase);

    private BrokerDashboardResponse? _dashboard;
    private DateTimeOffset _nextSnapshotAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSuccessfulQuoteAt = DateTimeOffset.MinValue;
    private int _trackedInstrumentCount;

    public KotakBrokerRealtimeService(
        KotakAuthenticationService authenticationService,
        KotakNeoClient client,
        KotakBrokerActivityService brokerActivityService,
        IHubContext<BrokerRealtimeHub> hub,
        ILogger<KotakBrokerRealtimeService> logger)
    {
        _authenticationService = authenticationService;
        _client = client;
        _brokerActivityService = brokerActivityService;
        _hub = hub;
        _logger = logger;
    }

    public bool IsConnected =>
        _lastSuccessfulQuoteAt != DateTimeOffset.MinValue &&
        DateTimeOffset.UtcNow - _lastSuccessfulQuoteAt < TimeSpan.FromSeconds(5);

    public int SubscriptionCount => _trackedInstrumentCount;

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _authenticationService.EnsureAuthenticatedAsync(
                    stoppingToken);

                if (_dashboard == null || DateTimeOffset.UtcNow >= _nextSnapshotAt)
                {
                    await RefreshBrokerSnapshotAsync(stoppingToken);
                }

                await RefreshLiveQuotesAsync(stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Kotak live quote refresh failed.");

                await PublishConnectionAsync(
                    false,
                    "Kotak live quote refresh failed: " + exception.Message,
                    stoppingToken);
            }

            try
            {
                await Task.Delay(QuoteInterval, stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RefreshBrokerSnapshotAsync(
        CancellationToken cancellationToken)
    {
        BrokerDashboardResponse dashboard =
            await _brokerActivityService.GetDashboardAsync(cancellationToken);

        await _stateGate.WaitAsync(cancellationToken);

        try
        {
            _dashboard = dashboard;
            _positionsByToken.Clear();

            foreach (BrokerPositionView position in dashboard.Positions)
            {
                if (string.IsNullOrWhiteSpace(position.InstrumentToken))
                {
                    continue;
                }

                string key = PositionKey(
                    position.ExchangeSegment,
                    position.InstrumentToken);

                _positionsByToken[key] = position;
            }

            _trackedInstrumentCount = BuildTrackedInstruments(dashboard).Count;

            bool hasWorkingOrder = dashboard.Orders.Any(x =>
                !IsFinalOrderStatus(x.Status));

            _nextSnapshotAt = DateTimeOffset.UtcNow +
                (hasWorkingOrder
                    ? WorkingOrderSnapshotInterval
                    : SnapshotInterval);
        }
        finally
        {
            _stateGate.Release();
        }

        dashboard.RealtimeConnected = IsConnected;
        dashboard.RealtimeSubscriptions = _trackedInstrumentCount;

        await _hub.Clients.All.SendAsync(
            "brokerSnapshot",
            dashboard,
            cancellationToken);
    }

    private async Task RefreshLiveQuotesAsync(
        CancellationToken cancellationToken)
    {
        BrokerDashboardResponse? dashboard;

        await _stateGate.WaitAsync(cancellationToken);

        try
        {
            dashboard = _dashboard;
        }
        finally
        {
            _stateGate.Release();
        }

        if (dashboard == null)
        {
            return;
        }

        List<QuoteInstrument> instruments = BuildTrackedInstruments(dashboard);
        _trackedInstrumentCount = instruments.Count;

        if (instruments.Count == 0)
        {
            _lastSuccessfulQuoteAt = DateTimeOffset.UtcNow;

            await PublishConnectionAsync(
                true,
                "No open Kotak instruments to quote.",
                cancellationToken);

            return;
        }

        using JsonDocument quotesJson = await _client.GetQuotesAsync(
            instruments,
            "ltp",
            cancellationToken);

        List<KotakQuoteView> quotes = ParseQuotes(quotesJson.RootElement);

        if (quotes.Count == 0)
        {
            throw new InvalidOperationException(
                "Kotak quote API returned no usable LTP values.");
        }

        DateTimeOffset tickTime = MarketClock.ToIndia(DateTimeOffset.UtcNow);
        _lastSuccessfulQuoteAt = DateTimeOffset.UtcNow;

        foreach (KotakQuoteView quote in quotes)
        {
            if (quote.LastPrice <= 0m || quote.LastPrice > 1_000_000m)
            {
                continue;
            }

            await ApplyQuoteAsync(
                quote,
                tickTime,
                cancellationToken);
        }

        await PublishConnectionAsync(
            true,
            $"Kotak live quotes updating {_trackedInstrumentCount} instrument(s) every second.",
            cancellationToken);
    }

    private async Task ApplyQuoteAsync(
        KotakQuoteView quote,
        DateTimeOffset tickTime,
        CancellationToken cancellationToken)
    {
        string exchange = NormalizeExchange(quote.ExchangeSegment);
        string key = PositionKey(exchange, quote.InstrumentToken);

        var update = new BrokerPositionTickUpdate
        {
            InstrumentToken = quote.InstrumentToken,
            ExchangeSegment = exchange,
            LastPrice = quote.LastPrice,
            TickTime = tickTime,
            Realtime = true
        };

        await _stateGate.WaitAsync(cancellationToken);

        try
        {
            if (_dashboard != null)
            {
                foreach (BrokerOrderView order in _dashboard.Orders.Where(x =>
                    string.Equals(
                        x.InstrumentToken,
                        quote.InstrumentToken,
                        StringComparison.OrdinalIgnoreCase) &&
                    NormalizeExchange(x.ExchangeSegment) == exchange))
                {
                    order.CurrentPremium = quote.LastPrice;
                    order.IsRealtime = true;
                    order.LastTickAt = tickTime;
                }
            }

            if (_positionsByToken.TryGetValue(key, out BrokerPositionView? position))
            {
                position.LastPrice = quote.LastPrice;
                position.Pnl = CalculateBrokerPositionPnl(position);
                position.IsRealtime = true;
                position.LastTickAt = tickTime;

                update.TradingSymbol = position.TradingSymbol;
                update.Pnl = position.Pnl;
                update.AveragePrice = position.AveragePrice;
                update.NetQuantity = position.NetQuantity;
            }
        }
        finally
        {
            _stateGate.Release();
        }

        await _hub.Clients.All.SendAsync(
            "positionTick",
            update,
            cancellationToken);
    }

    private static decimal CalculateBrokerPositionPnl(
        BrokerPositionView position)
    {
        if (position.NetQuantity == 0)
        {
            return position.Pnl;
        }

        if (position.TotalBuyAmount > 0m || position.TotalSellAmount > 0m)
        {
            decimal factor = position.PnlFactor > 0m
                ? position.PnlFactor
                : 1m;

            return (position.TotalSellAmount - position.TotalBuyAmount) +
                   (position.NetQuantity * position.LastPrice * factor);
        }

        if (position.AveragePrice <= 0m)
        {
            return position.Pnl;
        }

        return (position.LastPrice - position.AveragePrice) *
               position.NetQuantity;
    }

    private static List<QuoteInstrument> BuildTrackedInstruments(
        BrokerDashboardResponse dashboard)
    {
        var result = new HashSet<QuoteInstrument>();

        foreach (BrokerPositionView position in dashboard.Positions)
        {
            if (position.NetQuantity == 0 ||
                string.IsNullOrWhiteSpace(position.InstrumentToken))
            {
                continue;
            }

            result.Add(new QuoteInstrument(
                NormalizeExchange(position.ExchangeSegment),
                position.InstrumentToken));
        }

        foreach (BrokerOrderView order in dashboard.Orders)
        {
            if (IsFinalOrderStatus(order.Status) ||
                string.IsNullOrWhiteSpace(order.InstrumentToken))
            {
                continue;
            }

            result.Add(new QuoteInstrument(
                NormalizeExchange(order.ExchangeSegment),
                order.InstrumentToken));
        }

        return result.Take(50).ToList();
    }

    private static List<KotakQuoteView> ParseQuotes(JsonElement root)
    {
        var result = new List<KotakQuoteView>();

        foreach (JsonElement item in EnumerateObjects(root))
        {
            string token = StringValue(
                item,
                "exchange_token",
                "instrument_token",
                "tok",
                "tk");

            decimal ltp = DecimalValue(
                item,
                "ltp",
                "last_traded_price",
                "lastPrice");

            if (string.IsNullOrWhiteSpace(token) ||
                ltp <= 0m ||
                ltp > 1_000_000m)
            {
                continue;
            }

            result.Add(new KotakQuoteView
            {
                InstrumentToken = token,
                ExchangeSegment = StringValue(
                    item,
                    "exchange",
                    "exchange_segment",
                    "e",
                    "exSeg"),
                TradingSymbol = StringValue(
                    item,
                    "display_symbol",
                    "trading_symbol",
                    "trdSym",
                    "ts"),
                LastPrice = ltp
            });
        }

        return result
            .GroupBy(
                x => PositionKey(x.ExchangeSegment, x.InstrumentToken),
                StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();
    }

    private static IEnumerable<JsonElement> EnumerateObjects(JsonElement root)
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

    private static string StringValue(
        JsonElement element,
        params string[] names)
    {
        foreach (string name in names)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.ToString();
            }
        }

        return string.Empty;
    }

    private static decimal DecimalValue(
        JsonElement element,
        params string[] names)
    {
        string value = StringValue(element, names);

        if (decimal.TryParse(
            value,
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out decimal parsed))
        {
            return parsed;
        }

        return 0m;
    }

    private Task PublishConnectionAsync(
        bool connected,
        string message,
        CancellationToken cancellationToken)
    {
        return _hub.Clients.All.SendAsync(
            "brokerConnection",
            new BrokerRealtimeConnectionStatus
            {
                Connected = connected,
                SubscribedInstruments = _trackedInstrumentCount,
                Timestamp = MarketClock.ToIndia(DateTimeOffset.UtcNow),
                Message = message
            },
            cancellationToken);
    }

    private static bool IsFinalOrderStatus(string status)
    {
        string value = status.Trim().ToUpperInvariant();

        return value.Contains("COMPLETE") ||
               value.Contains("FILLED") ||
               value.Contains("REJECT") ||
               value.Contains("CANCEL");
    }

    private static string NormalizeExchange(string exchange)
    {
        if (string.IsNullOrWhiteSpace(exchange))
        {
            return "nse_fo";
        }

        string normalized = exchange.Trim().ToLowerInvariant();

        if (normalized is "nfo" or "nsefo" or "nse_fo")
        {
            return "nse_fo";
        }

        return normalized;
    }

    private static string PositionKey(
        string exchange,
        string instrumentToken)
    {
        return $"{NormalizeExchange(exchange)}|{instrumentToken}";
    }
}
