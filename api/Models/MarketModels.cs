namespace NiftyPremiumTrader.Api.Models;

public sealed class OptionCandle
{
    public DateOnly SessionDate { get; set; }
    public DateTimeOffset CandleTime { get; set; }
    public string StockCode { get; set; } = "NIFTY";
    public string ExchangeCode { get; set; } = "NFO";
    public string ProductType { get; set; } = "options";
    public DateOnly? ExpiryDate { get; set; }
    public decimal? Strike { get; set; }
    public string? OptionType { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public long Volume { get; set; }
    public long OpenInterest { get; set; }
    public bool IsComplete { get; set; } = true;
    public decimal? Delta { get; set; }
    public string DataSource { get; set; } = "BREEZE";
}

public sealed class CandlePair
{
    public int PairNumber { get; set; }
    public decimal Strike { get; set; }
    public decimal CallStrike { get; set; }
    public decimal PutStrike { get; set; }
    public OptionCandle? Call { get; set; }
    public OptionCandle? Put { get; set; }
}

public sealed class HedgePreview
{
    public string OptionType { get; set; } = string.Empty;
    public decimal SoldStrike { get; set; }
    public int HedgeDistance { get; set; }
    public decimal HedgeStrike { get; set; }
    public decimal? Premium { get; set; }
    public DateTimeOffset? PriceTime { get; set; }
    public string PriceSource { get; set; } = string.Empty;
    public bool IsAvailable { get; set; }
    public string Message { get; set; } = string.Empty;
}
