namespace NiftyPremiumTrader.Api.Options;

public sealed class BreezeOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;
    public string SessionToken { get; set; } = string.Empty;
    public bool UseSessionMaster { get; set; } = true;
    public string StockCode { get; set; } = "NIFTY";
    public string ExchangeCode { get; set; } = "NFO";
    public string IndexExchangeCode { get; set; } = "NSE";
}


public sealed class KotakNeoOptions
{
    public string Environment { get; set; } = "prod";
    public string ConsumerKey { get; set; } = string.Empty;
    public string MobileNumber { get; set; } = string.Empty;
    public string Ucc { get; set; } = string.Empty;
    public string Mpin { get; set; } = string.Empty;
    public string TotpSecret { get; set; } = string.Empty;
    public string NeoFinKey { get; set; } = "neotradeapi";
    public string Product { get; set; } = "MIS";
    public string ExchangeSegment { get; set; } = "nse_fo";
    public bool AutoLogin { get; set; } = true;
}

public sealed class ApiBudgetOptions
{
    public int DailyLimit { get; set; } = 2000;
    public int ReservedForLive { get; set; } = 900;
    public int LiveSafetyCalls { get; set; } = 150;
}

public sealed class StrategyOptions
{
    public int StrikeStep { get; set; } = 100;
    public string LegacySchema { get; set; } = "trading";
    public bool LegacyTimesAreUtc { get; set; } = true;
    public decimal MinimumAverageWeakerBodyPercent { get; set; } = 0m; // Optional research filter; locked rule stays 1%.
    public decimal PairMovePercent { get; set; } = 1.0m;
    public int MinimumAgreeingPairs { get; set; } = 2;
    public int Lots { get; set; } = 7;
    public int LotSize { get; set; } = 65;
    public TimeSpan EntryStart { get; set; } = new(9, 25, 0);
    public TimeSpan LastEntry { get; set; } = new(14, 0, 0);
    public TimeSpan EndOfDayExit { get; set; } = new(15, 15, 0);
    public int CooldownCandles { get; set; } = 2;
    public decimal ProfitTarget { get; set; } = 25000m;
    public decimal HedgeTriggerLoss { get; set; } = -5000m;
    public decimal NakedCatastrophicStop { get; set; } = -25000m;
    public decimal HedgedStop { get; set; } = -15000m;
    public int DefaultHedgeDistance { get; set; } = 300;
    public decimal NakedRoundTripCost { get; set; } = 500m;
    public decimal HedgedRoundTripCost { get; set; } = 1000m;
}

public sealed class LiveTradingOptions
{
    public bool Enabled { get; set; } = true;
    public bool AllowBrokerOrders { get; set; } = false;
    public int WorkerPollSeconds { get; set; } = 2;
}

public sealed class PositionSizingOptions
{
 public int MaximumLots { get; set; } = 15;
 public decimal MarginReservePercent { get; set; } = 10m;
 public decimal HedgeCashReservePerLot { get; set; } = 1000m;
 public decimal MaximumNakedStopBudget { get; set; } = 0m;
 public string DefaultProfile { get; set; } = "ScaledWinner";
 public string LimitsPath { get; set; } = "/quick/user/limits";
 public string MarginPath { get; set; } = "/quick/user/check-margin";
}
