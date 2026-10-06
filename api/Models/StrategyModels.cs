namespace NiftyPremiumTrader.Api.Models;

public enum SignalDirection
{
    None = 0,
    Bullish = 1,
    Bearish = 2
}

public enum PendingActionType
{
    None = 0,
    Enter = 1,
    Hedge = 2,
    Exit = 3
}

public sealed class PairSignalDetail
{
    public int PairNumber { get; set; }
    public decimal CallStrike { get; set; }
    public decimal PutStrike { get; set; }
    public decimal Strike { get; set; }
    public decimal CallOpen { get; set; }
    public decimal CallClose { get; set; }
    public decimal CallChangePercent { get; set; }
    public decimal PutOpen { get; set; }
    public decimal PutClose { get; set; }
    public decimal PutChangePercent { get; set; }
    public string PairSignal { get; set; } = "NONE";
}

public sealed class PremiumSignal
{
    public DateTimeOffset CandleTime { get; set; }
    public SignalDirection Direction { get; set; }
    public int BullishPairs { get; set; }
    public int BearishPairs { get; set; }
    public decimal SelectedAtmStrike { get; set; }
    public string StrikeSelectionSource { get; set; } = "DELTA";
    public decimal AverageWeakerBodyPercent { get; set; }
    public bool StrengthFilterPassed { get; set; } = true;
    public List<decimal> BullishStrikes { get; set; } = [];
    public List<decimal> BearishStrikes { get; set; } = [];
    public List<PairSignalDetail> PairDetails { get; set; } = [];
}

public sealed class PendingStrategyAction
{
    public PendingActionType Type { get; set; }
    public DateTimeOffset ExecuteTime { get; set; }
    public SignalDirection Direction { get; set; }
    public string OptionType { get; set; } = string.Empty;
    public decimal Strike { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed class StrategyTrade
{
    public Guid TradeId { get; set; } = Guid.NewGuid();
    public NiftyPremiumTrader.Api.Services.RiskSetup? RiskSetup { get; set; }
    public DateOnly SessionDate { get; set; }
    public SignalDirection Direction { get; set; }
    public DateTimeOffset SignalTime { get; set; }
    public string SoldOptionType { get; set; } = string.Empty;
    public decimal SoldStrike { get; set; }
    public DateTimeOffset EntryTime { get; set; }
    public decimal EntryPrice { get; set; }
    public int Lots { get; set; }
    public int LotSize { get; set; }
    public int Quantity { get; set; }
    public int HedgeDistance { get; set; }
    public decimal? HedgeStrike { get; set; }
    public DateTimeOffset? HedgeEntryTime { get; set; }
    public decimal? HedgeEntryPrice { get; set; }
    public decimal? HedgeTriggerPnl { get; set; }
    public DateTimeOffset? ExitTime { get; set; }
    public decimal? SoldExitPrice { get; set; }
    public decimal? HedgeExitPrice { get; set; }
    public decimal GrossPnl { get; set; }
    public decimal Costs { get; set; }
    public decimal NetPnl { get; set; }
    public decimal BestMarkPnl { get; set; }
    public decimal WorstMarkPnl { get; set; }
    public string ExitReason { get; set; } = string.Empty;
    public string SoldEntryOrderId { get; set; } = string.Empty;
    public string HedgeEntryOrderId { get; set; } = string.Empty;
    public string SoldExitOrderId { get; set; } = string.Empty;
    public string HedgeExitOrderId { get; set; } = string.Empty;
    public bool BrokerExecution { get; set; }

    public bool IsOpen => ExitTime == null;
    public bool IsHedged => HedgeEntryTime != null;

    public int DurationMinutes
    {
        get
        {
            DateTimeOffset end = ExitTime ?? DateTimeOffset.UtcNow;
            return Math.Max(0, (int)(end - EntryTime).TotalMinutes);
        }
    }

    public decimal? TheoreticalMaxLossAfterHedge
    {
        get
        {
            if (!HedgeEntryPrice.HasValue)
            {
                return null;
            }

            decimal creditPoints = EntryPrice - HedgeEntryPrice.Value;
            decimal riskPoints = HedgeDistance - creditPoints;
            decimal risk = riskPoints * Quantity;
            return Math.Max(0m, risk);
        }
    }
}

public sealed class DayBacktestResult
{
    public List<string> DataMessages { get; set; } = [];
    public DateOnly Date { get; set; }
    public DateOnly ExpiryDate { get; set; }
    public DateTimeOffset? CurrentTime { get; set; }
    public PremiumSignal? LastSignal { get; set; }
    public StrategyTrade? ActiveTrade { get; set; }
    public PendingStrategyAction? PendingAction { get; set; }
    public List<StrategyTrade> Trades { get; set; } = [];

    public decimal NetPnl => Trades.Sum(x => x.NetPnl);
    public decimal GrossPnl => Trades.Sum(x => x.GrossPnl);
    public decimal Costs => Trades.Sum(x => x.Costs);
    public int Wins => Trades.Count(x => x.NetPnl > 0m);
    public int Losses => Trades.Count(x => x.NetPnl <= 0m);
}

public sealed class RangeBacktestResult
{
    public DateOnly FromDate { get; set; }
    public DateOnly ToDate { get; set; }
    public List<DayBacktestResult> Days { get; set; } = [];
    public List<string> Messages { get; set; } = [];

    public List<StrategyTrade> Trades => Days.SelectMany(x => x.Trades).ToList();
    public decimal NetPnl => Days.Sum(x => x.NetPnl);
    public decimal GrossPnl => Days.Sum(x => x.GrossPnl);
    public decimal Costs => Days.Sum(x => x.Costs);
    public int TotalTrades => Trades.Count;
    public int WinningTrades => Trades.Count(x => x.NetPnl > 0m);
    public decimal WinRate => TotalTrades == 0 ? 0m : WinningTrades * 100m / TotalTrades;
    public decimal AveragePnlPerTrade => TotalTrades == 0 ? 0m : NetPnl / TotalTrades;
    public int HedgedTrades => Trades.Count(x => x.IsHedged);
    public decimal HedgeUsageRate => TotalTrades == 0 ? 0m : HedgedTrades * 100m / TotalTrades;
    public decimal AverageDefinedRiskAfterHedge => CalculateAverageDefinedRisk();
    public decimal MaxDrawdown => CalculateMaxDrawdown();
    public int ProfitableDays => Days.Count(x => x.NetPnl > 0m);

    private decimal CalculateAverageDefinedRisk()
    {
        List<decimal> values = Trades
            .Where(x => x.TheoreticalMaxLossAfterHedge.HasValue)
            .Select(x => x.TheoreticalMaxLossAfterHedge!.Value)
            .ToList();

        if (values.Count == 0)
        {
            return 0m;
        }

        return values.Average();
    }

    private decimal CalculateMaxDrawdown()
    {
        decimal equity = 0m;
        decimal peak = 0m;
        decimal maxDrawdown = 0m;

        foreach (StrategyTrade trade in Trades.OrderBy(x => x.ExitTime))
        {
            equity += trade.NetPnl;

            if (equity > peak)
            {
                peak = equity;
            }

            decimal drawdown = peak - equity;

            if (drawdown > maxDrawdown)
            {
                maxDrawdown = drawdown;
            }
        }

        return maxDrawdown;
    }
}

public sealed class StepBacktestResponse
{
    public Guid SessionId { get; set; }
    public DateOnly Date { get; set; }
    public DateOnly ExpiryDate { get; set; }
    public DateTimeOffset CurrentTime { get; set; }
    public int CurrentIndex { get; set; }
    public int TotalSteps { get; set; }
    public PremiumSignal? Signal { get; set; }
    public PendingStrategyAction? PendingAction { get; set; }
    public StrategyTrade? ActiveTrade { get; set; }
    public List<StrategyTrade> CompletedTrades { get; set; } = [];
    public decimal RealizedPnl { get; set; }
    public decimal UnrealizedPnl { get; set; }
}

public sealed class StartBacktestRequest
{
    public string? SetupProfile { get; set; }
    public DateOnly Date { get; set; }
    public DateOnly? ExpiryDate { get; set; }
    public int HedgeDistance { get; set; } = 300;
    public int Lots { get; set; } = 7;
    public int LotSize { get; set; } = 65;
}

public sealed class RangeBacktestRequest
{
    public string? SetupProfile { get; set; }
    public DateOnly FromDate { get; set; }
    public DateOnly ToDate { get; set; }
    public int HedgeDistance { get; set; } = 300;
    public int Lots { get; set; } = 7;
    public int LotSize { get; set; } = 65;
}

public sealed class ApiBudgetStatus
{
    public DateOnly Date { get; set; }
    public int Used { get; set; }
    public int Limit { get; set; }
    public int Remaining { get; set; }
    public int ReservedForLive { get; set; }
    public int HistoricalRemaining { get; set; }
}

public sealed class LiveBudgetPlan
{
    public int RefreshMinutes { get; set; }
    public int ContractsPerRefresh { get; set; }
    public int EstimatedRemainingRefreshes { get; set; }
    public int EstimatedCallsNeeded { get; set; }
    public int AvailableCalls { get; set; }
    public bool IsDegraded => RefreshMinutes > 5;
}
