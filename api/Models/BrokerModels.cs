namespace NiftyPremiumTrader.Api.Models;

public sealed class BrokerOrderView
{
    public string OrderId { get; set; } = string.Empty;
    public string TradingSymbol { get; set; } = string.Empty;
    public string InstrumentToken { get; set; } = string.Empty;
    public string ExchangeSegment { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public string OrderType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public int FilledQuantity { get; set; }
    public decimal Price { get; set; }
    public decimal AveragePrice { get; set; }
    public string Product { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public string Source { get; set; } = "MANUAL_APP";
    public decimal CurrentPremium { get; set; }
    public bool IsRealtime { get; set; }
    public DateTimeOffset? LastTickAt { get; set; }
}

public sealed class BrokerTradeView
{
    public string TradeId { get; set; } = string.Empty;
    public string OrderId { get; set; } = string.Empty;
    public string TradingSymbol { get; set; } = string.Empty;
    public string InstrumentToken { get; set; } = string.Empty;
    public string ExchangeSegment { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal Price { get; set; }
    public string Source { get; set; } = "MANUAL_APP";
}

public sealed class BrokerPositionView
{
    public string TradingSymbol { get; set; } = string.Empty;
    public string InstrumentToken { get; set; } = string.Empty;
    public string ExchangeSegment { get; set; } = string.Empty;
    public string Product { get; set; } = string.Empty;
    public DateOnly? ExpiryDate { get; set; }
    public decimal? Strike { get; set; }
    public string OptionType { get; set; } = string.Empty;
    public string PositionRole { get; set; } = string.Empty;
    public int NetQuantity { get; set; }
    public int BuyQuantity { get; set; }
    public int SellQuantity { get; set; }
    public decimal AveragePrice { get; set; }
    public decimal LastPrice { get; set; }
    public decimal Pnl { get; set; }
    public decimal TotalBuyAmount { get; set; }
    public decimal TotalSellAmount { get; set; }
    public decimal PnlFactor { get; set; } = 1m;
    public int LotSize { get; set; }
    public string Source { get; set; } = "MANUAL_APP";
    public bool CanExitFromUi { get; set; } = true;
    public string ExitRestriction { get; set; } = string.Empty;
    public bool IsRealtime { get; set; }
    public DateTimeOffset? LastTickAt { get; set; }
}

public sealed class BrokerDashboardResponse
{
    public DateTimeOffset RefreshedAt { get; set; }
    public List<BrokerPositionView> Positions { get; set; } = [];
    public List<BrokerOrderView> Orders { get; set; } = [];
    public List<BrokerTradeView> Trades { get; set; } = [];
    public string Message { get; set; } = string.Empty;
    public bool RealtimeConnected { get; set; }
    public int RealtimeSubscriptions { get; set; }
}

public sealed class ManualBrokerOrderRequest
{
    public DateOnly ExpiryDate { get; set; }
    public decimal Strike { get; set; }
    public string OptionType { get; set; } = "CE";
    public string Side { get; set; } = "BUY";
    public int Lots { get; set; } = 1;
    public int LotSize { get; set; } = 65;
    public string OrderType { get; set; } = "MARKET";
    public decimal? LimitPrice { get; set; }
}

public sealed class ExitBrokerPositionRequest
{
    public string TradingSymbol { get; set; } = string.Empty;
    public string OrderType { get; set; } = "MARKET";
    public decimal? LimitPrice { get; set; }
}

public sealed class ManualBrokerOrderRecord
{
    public Guid LocalOrderId { get; set; }
    public string BrokerOrderId { get; set; } = string.Empty;
    public DateOnly SessionDate { get; set; }
    public string TradingSymbol { get; set; } = string.Empty;
    public string InstrumentToken { get; set; } = string.Empty;
    public decimal? Strike { get; set; }
    public string OptionType { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public int Lots { get; set; }
    public int LotSize { get; set; }
    public string OrderType { get; set; } = string.Empty;
    public decimal? LimitPrice { get; set; }
    public string Purpose { get; set; } = "ENTRY";
    public string Status { get; set; } = string.Empty;
    public decimal? AveragePrice { get; set; }
}
