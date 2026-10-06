namespace NiftyPremiumTrader.Api.Models;

public sealed record WsToken(
    string ExchangeSegment,
    string InstrumentToken)
{
    public string InputToken => $"{ExchangeSegment}|{InstrumentToken}";
}

public sealed record DepthLevel(
    long Quantity,
    decimal Price,
    int Orders);

public sealed class MarketTick
{
    public string ExchangeSegment { get; init; } = string.Empty;
    public string InstrumentToken { get; init; } = string.Empty;
    public string? TradingSymbol { get; set; }
    public int Level { get; init; }
    public decimal LastTradedPrice { get; init; }
    public decimal Open { get; init; }
    public decimal High { get; init; }
    public decimal Low { get; init; }
    public decimal Close { get; init; }
    public decimal AverageTradePrice { get; init; }
    public long LastTradeTime { get; init; }
    public long LastUpdateTime { get; init; }
    public long LastTradeQuantity { get; init; }
    public long TotalBuyQuantity { get; init; }
    public long TotalSellQuantity { get; init; }
    public long VolumeTradedToday { get; init; }
    public long OpenInterest { get; init; }
    public decimal NetChange { get; init; }
    public decimal NetChangePercent { get; init; }
    public decimal UpperCircuitLimit { get; init; }
    public decimal LowerCircuitLimit { get; init; }
    public decimal YearlyHigh { get; init; }
    public decimal YearlyLow { get; init; }
    public decimal TotalTradedValue { get; init; }
    public int MarketLot { get; init; }
    public int Precision { get; init; }
    public int Multiplier { get; init; }
    public bool Auction { get; init; }
    public List<DepthLevel> Buy { get; init; } = [];
    public List<DepthLevel> Sell { get; init; } = [];
    public DateTime ReceivedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record IndexTick(
    string ExchangeSegment,
    string InstrumentToken,
    string Name,
    decimal LastTradedPrice,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Change,
    decimal NetChangePercent,
    long LastTradeTime);

public sealed record MarketStatusTick(
    string ExchangeSegment,
    int StatusCode,
    string Status);

public sealed class BrokerPositionTickUpdate
{
    public string TradingSymbol { get; set; } = string.Empty;
    public string InstrumentToken { get; set; } = string.Empty;
    public string ExchangeSegment { get; set; } = string.Empty;
    public decimal LastPrice { get; set; }
    public decimal Pnl { get; set; }
    public decimal AveragePrice { get; set; }
    public int NetQuantity { get; set; }
    public DateTimeOffset TickTime { get; set; }
    public bool Realtime { get; set; }
}

public sealed class BrokerRealtimeConnectionStatus
{
    public bool Connected { get; set; }
    public int SubscribedInstruments { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed record QuoteInstrument(
    string ExchangeSegment,
    string InstrumentToken);

public sealed class KotakQuoteView
{
    public string ExchangeSegment { get; set; } = string.Empty;
    public string InstrumentToken { get; set; } = string.Empty;
    public string TradingSymbol { get; set; } = string.Empty;
    public decimal LastPrice { get; set; }
}
