using Microsoft.Extensions.Options;
using NiftyPremiumTrader.Api.Data;
using NiftyPremiumTrader.Api.Models;
using NiftyPremiumTrader.Api.Options;

namespace NiftyPremiumTrader.Api.Services;

public sealed class StrategyEngine
{
    private readonly ICandleStore _repository;
    private readonly CandleDataService _dataService;
    private readonly PremiumSignalService _signalService;
    private readonly StrategyOptions _options;

    public StrategyEngine(
        ICandleStore repository,
        CandleDataService dataService,
        PremiumSignalService signalService,
        IOptions<StrategyOptions> options)
    {
        _repository = repository;
        _dataService = dataService;
        _signalService = signalService;
        _options = options.Value;
    }

    public async Task<DayBacktestResult> RunDayAsync(
        DateOnly date,
        DateOnly expiryDate,
        int hedgeDistance,
        int lots,
        int lotSize,
        int? stopAfterIndex,
        CancellationToken cancellationToken,
        RiskSetup? setup = null)
    {
        if(setup != null)
        {
            var clone = System.Text.Json.JsonSerializer.Deserialize<StrategyOptions>(System.Text.Json.JsonSerializer.Serialize(_options))!;
            clone.ProfitTarget=setup.ProfitTarget;clone.HedgeTriggerLoss=setup.HedgeTriggerLoss;
            clone.NakedCatastrophicStop=setup.NakedCatastrophicStop;clone.HedgedStop=setup.HedgedStop;clone.CooldownCandles=setup.CooldownCandles;
            var scoped=new StrategyEngine(_repository,_dataService,_signalService,Microsoft.Extensions.Options.Options.Create(clone));
            var scopedResult=await scoped.RunDayAsync(date,expiryDate,hedgeDistance,lots,lotSize,stopAfterIndex,cancellationToken);
            foreach(var trade in scopedResult.Trades)trade.RiskSetup=setup;
            if(scopedResult.ActiveTrade != null)scopedResult.ActiveTrade.RiskSetup=setup;
            return scopedResult;
        }
        _dataService.ValidateHedgeDistance(hedgeDistance);
        ValidatePositionSize(lots, lotSize);

        // Historical data is prepared once when the replay/range day starts.
        // Replay steps are database-only and must never call ICICI again.
        List<SignalPairDefinition> signalStrikes = await _dataService.GetSignalPairsFromDatabaseAsync(
            date,
            cancellationToken);

        Dictionary<decimal, List<OptionCandle>> calls = await LoadContractsAsync(
            date,
            expiryDate,
            signalStrikes.Select(p=>p.CallStrike),
            "CE",
            cancellationToken);

        Dictionary<decimal, List<OptionCandle>> puts = await LoadContractsAsync(
            date,
            expiryDate,
            signalStrikes.Select(p=>p.PutStrike),
            "PE",
            cancellationToken);

        List<DateTimeOffset> timeline = BuildReplayTimeline(
            date,
            signalStrikes,
            calls,
            puts);

        if (timeline.Count == 0)
        {
            throw new InvalidOperationException(
                $"No common 5-minute CE/PE candle timeline exists for {date}.");
        }

        int lastIndex = timeline.Count - 1;

        if (stopAfterIndex.HasValue)
        {
            lastIndex = Math.Clamp(
                stopAfterIndex.Value,
                0,
                timeline.Count - 1);
        }

        var result = new DayBacktestResult
        {
            Date = date,
            ExpiryDate = expiryDate
        };

        StrategyTrade? activeTrade = null;
        PendingStrategyAction? pendingAction = null;
        SignalDirection previousDirection = SignalDirection.None;
        int cooldownRemaining = 0;

        for (int index = 0; index <= lastIndex; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DateTimeOffset time = timeline[index];
            result.CurrentTime = time;

            List<CandlePair> pairs = BuildPairsAtTime(
                signalStrikes,
                calls,
                puts,
                time);

            PremiumSignal signal = _signalService.Evaluate(
                time,
                pairs);

            result.LastSignal = signal;

            bool freshDirection = signal.Direction != previousDirection;
            previousDirection = signal.Direction;

            if (pendingAction != null &&
                pendingAction.ExecuteTime == time)
            {
                var actionToExecute = pendingAction;
                bool executingExit = actionToExecute.Type == PendingActionType.Exit;
                (activeTrade, pendingAction, cooldownRemaining) =
                    await ExecutePendingActionAsync(
                        actionToExecute,
                        activeTrade,
                        result,
                        date,
                        expiryDate,
                        hedgeDistance,
                        lots,
                        lotSize,
                        cooldownRemaining,
                        cancellationToken);
                if (executingExit && activeTrade == null) continue;
            }

            TimeOnly indiaTime = TimeOnly.FromDateTime(
                MarketClock.ToIndia(time).DateTime);

            if (activeTrade != null &&
                indiaTime >= TimeOnly.FromTimeSpan(_options.EndOfDayExit))
            {
                bool closed = await CloseAtCurrentOpenAsync(
                    activeTrade,
                    result,
                    date,
                    expiryDate,
                    time,
                    "EOD",
                    cancellationToken);

                if (closed)
                {
                    activeTrade = null;
                    pendingAction = null;
                    cooldownRemaining = _options.CooldownCandles;
                }

                continue;
            }

            if (activeTrade != null)
            {
                decimal markPnl = await UpdateCurrentMarkAsync(
                    activeTrade,
                    date,
                    expiryDate,
                    time,
                    cancellationToken);

                if (index < timeline.Count - 1 &&
                    pendingAction == null)
                {
                    pendingAction = await BuildRiskActionAsync(
                        activeTrade,
                        markPnl,
                        timeline[index + 1],
                        date,
                        expiryDate,
                        hedgeDistance,
                        cancellationToken);
                }

                continue;
            }

            if (pendingAction != null)
            {
                continue;
            }

            if (cooldownRemaining > 0)
            {
                cooldownRemaining--;
                continue;
            }

            if (!CanEnterAt(indiaTime))
            {
                continue;
            }

            if (signal.Direction == SignalDirection.None)
            {
                continue;
            }

            if (!freshDirection || !signal.StrengthFilterPassed)
            {
                continue;
            }

            if (index >= timeline.Count - 1)
            {
                continue;
            }

            string optionType = signal.Direction == SignalDirection.Bullish
                ? "PE"
                : "CE";

            if (signal.SelectedAtmStrike <= 0m)
            {
                continue;
            }

            pendingAction = new PendingStrategyAction
            {
                Type = PendingActionType.Enter,
                ExecuteTime = timeline[index + 1],
                Direction = signal.Direction,
                OptionType = optionType,
                Strike = signal.SelectedAtmStrike,
                Reason = "PREMIUM_SIGNAL"
            };


        }

        if(activeTrade!=null && !activeTrade.IsHedged)
        {
            decimal hedgeStrike=_dataService.GetHedgeStrike(activeTrade.SoldStrike,activeTrade.SoldOptionType,hedgeDistance);
            if(!await _dataService.IsSelectedContractAsync(date,hedgeStrike,activeTrade.SoldOptionType,cancellationToken))
                result.DataMessages.Add($"Hedge {hedgeStrike} {activeTrade.SoldOptionType} is outside the fixed morning set; naked stop applies.");
        }
        result.ActiveTrade = activeTrade;
        result.PendingAction = pendingAction;

        if (!stopAfterIndex.HasValue && activeTrade != null)
        {
            DateTimeOffset lastTime = timeline[lastIndex];

            bool closed = await CloseAtCurrentOpenAsync(
                activeTrade,
                result,
                date,
                expiryDate,
                lastTime,
                "INCOMPLETE_DATA",
                cancellationToken);

            if (closed)
            {
                result.ActiveTrade = null;
                result.PendingAction = null;
            }
        }

        return result;
    }

    public async Task<List<DateTimeOffset>> GetTimelineAsync(
        DateOnly date,
        DateOnly expiryDate,
        CancellationToken cancellationToken)
    {
        // Data was already prepared by BacktestService.StartAsync.
        // This method only reads PostgreSQL.
        List<SignalPairDefinition> strikes = await _dataService.GetSignalPairsFromDatabaseAsync(
            date,
            cancellationToken);

        Dictionary<decimal, List<OptionCandle>> calls = await LoadContractsAsync(
            date,
            expiryDate,
            strikes.Select(p=>p.CallStrike),
            "CE",
            cancellationToken);

        Dictionary<decimal, List<OptionCandle>> puts = await LoadContractsAsync(
            date,
            expiryDate,
            strikes.Select(p=>p.PutStrike),
            "PE",
            cancellationToken);

        return BuildReplayTimeline(
            date,
            strikes,
            calls,
            puts);
    }

    private async Task<(StrategyTrade? ActiveTrade, PendingStrategyAction? PendingAction, int Cooldown)>
        ExecutePendingActionAsync(
            PendingStrategyAction pendingAction,
            StrategyTrade? activeTrade,
            DayBacktestResult result,
            DateOnly date,
            DateOnly expiryDate,
            int hedgeDistance,
            int lots,
            int lotSize,
            int cooldownRemaining,
            CancellationToken cancellationToken)
    {
        if (pendingAction.Type == PendingActionType.Enter)
        {
            OptionCandle? entryCandle = await _repository.GetOptionCandleAsync(
                date,
                expiryDate,
                pendingAction.Strike,
                pendingAction.OptionType,
                pendingAction.ExecuteTime,
                cancellationToken);

            if (entryCandle == null)
            {
                return (null, null, cooldownRemaining);
            }

            activeTrade = new StrategyTrade
            {
                SessionDate = date,
                Direction = pendingAction.Direction,
                SignalTime = pendingAction.ExecuteTime.AddMinutes(-5),
                SoldOptionType = pendingAction.OptionType,
                SoldStrike = pendingAction.Strike,
                EntryTime = pendingAction.ExecuteTime,
                EntryPrice = entryCandle.Open,
                Lots = lots,
                LotSize = lotSize,
                Quantity = lots * lotSize,
                HedgeDistance = hedgeDistance,
                BestMarkPnl = 0m,
                WorstMarkPnl = 0m
            };

            return (activeTrade, null, cooldownRemaining);
        }

        if (activeTrade == null)
        {
            return (null, null, cooldownRemaining);
        }

        if (pendingAction.Type == PendingActionType.Hedge)
        {
            OptionCandle? hedgeCandle = await _repository.GetOptionCandleAsync(
                date,
                expiryDate,
                pendingAction.Strike,
                pendingAction.OptionType,
                pendingAction.ExecuteTime,
                cancellationToken);

            if (hedgeCandle == null)
            {
                return (activeTrade, null, cooldownRemaining);
            }

            activeTrade.HedgeStrike = pendingAction.Strike;
            activeTrade.HedgeEntryTime = pendingAction.ExecuteTime;
            activeTrade.HedgeEntryPrice = hedgeCandle.Open;

            return (activeTrade, null, cooldownRemaining);
        }

        if (pendingAction.Type == PendingActionType.Exit)
        {
            bool closed = await CloseAtCurrentOpenAsync(
                activeTrade,
                result,
                date,
                expiryDate,
                pendingAction.ExecuteTime,
                pendingAction.Reason,
                cancellationToken);

            if (closed)
            {
                return (null, null, _options.CooldownCandles);
            }

            return (activeTrade, null, cooldownRemaining);
        }

        return (activeTrade, null, cooldownRemaining);
    }

    private async Task<PendingStrategyAction?> BuildRiskActionAsync(
        StrategyTrade trade,
        decimal markPnl,
        DateTimeOffset nextTime,
        DateOnly date,
        DateOnly expiryDate,
        int hedgeDistance,
        CancellationToken cancellationToken)
    {
        if (markPnl >= _options.ProfitTarget)
        {
            return new PendingStrategyAction
            {
                Type = PendingActionType.Exit,
                ExecuteTime = nextTime,
                Direction = trade.Direction,
                OptionType = trade.SoldOptionType,
                Strike = trade.SoldStrike,
                Reason = "TARGET"
            };
        }

        if (!trade.IsHedged &&
            markPnl <= _options.NakedCatastrophicStop)
        {
            return new PendingStrategyAction
            {
                Type = PendingActionType.Exit,
                ExecuteTime = nextTime,
                Direction = trade.Direction,
                OptionType = trade.SoldOptionType,
                Strike = trade.SoldStrike,
                Reason = "NAKED_STOP"
            };
        }

        if (!trade.IsHedged &&
            markPnl <= _options.HedgeTriggerLoss)
        {
            decimal hedgeStrike = _dataService.GetHedgeStrike(
                trade.SoldStrike,
                trade.SoldOptionType,
                hedgeDistance);

            // Replay never downloads. Outside-set hedges remain unavailable,
            // matching the original eight-contract backtest.
            if (!await _dataService.IsSelectedContractAsync(date,hedgeStrike,trade.SoldOptionType,cancellationToken))
                return null;
            var storedHedge=await _repository.GetOptionCandleAsync(date,expiryDate,hedgeStrike,trade.SoldOptionType,nextTime,cancellationToken);
            if (storedHedge==null) return null;

            trade.HedgeTriggerPnl = markPnl;

            return new PendingStrategyAction
            {
                Type = PendingActionType.Hedge,
                ExecuteTime = nextTime,
                Direction = trade.Direction,
                OptionType = trade.SoldOptionType,
                Strike = hedgeStrike,
                Reason = "HEDGE_TRIGGER"
            };
        }

        if (trade.IsHedged &&
            markPnl <= _options.HedgedStop)
        {
            return new PendingStrategyAction
            {
                Type = PendingActionType.Exit,
                ExecuteTime = nextTime,
                Direction = trade.Direction,
                OptionType = trade.SoldOptionType,
                Strike = trade.SoldStrike,
                Reason = "HEDGED_STOP"
            };
        }

        return null;
    }

    private async Task<decimal> UpdateCurrentMarkAsync(
        StrategyTrade trade,
        DateOnly date,
        DateOnly expiryDate,
        DateTimeOffset time,
        CancellationToken cancellationToken)
    {
        OptionCandle? soldCandle = await _repository.GetOptionCandleAsync(
            date,
            expiryDate,
            trade.SoldStrike,
            trade.SoldOptionType,
            time,
            cancellationToken);

        if (soldCandle == null)
        {
            return trade.GrossPnl;
        }

        decimal hedgePrice = 0m;

        if (trade.IsHedged && trade.HedgeStrike.HasValue)
        {
            OptionCandle? hedgeCandle = await _repository.GetOptionCandleAsync(
                date,
                expiryDate,
                trade.HedgeStrike.Value,
                trade.SoldOptionType,
                time,
                cancellationToken);

            if (hedgeCandle != null)
            {
                hedgePrice = hedgeCandle.Close;
            }
        }

        decimal markPnl = CalculateGrossPnl(
            trade,
            soldCandle.Close,
            hedgePrice);

        trade.GrossPnl = markPnl;
        trade.Costs = trade.IsHedged
            ? _options.HedgedRoundTripCost
            : _options.NakedRoundTripCost;
        trade.NetPnl = markPnl - trade.Costs;

        if (markPnl > trade.BestMarkPnl)
        {
            trade.BestMarkPnl = markPnl;
        }

        if (markPnl < trade.WorstMarkPnl)
        {
            trade.WorstMarkPnl = markPnl;
        }

        return markPnl;
    }

    private async Task<bool> CloseAtCurrentOpenAsync(
        StrategyTrade trade,
        DayBacktestResult result,
        DateOnly date,
        DateOnly expiryDate,
        DateTimeOffset time,
        string reason,
        CancellationToken cancellationToken)
    {
        OptionCandle? soldExit = await _repository.GetOptionCandleAsync(
            date,
            expiryDate,
            trade.SoldStrike,
            trade.SoldOptionType,
            time,
            cancellationToken);

        if (soldExit == null)
        {
            return false;
        }

        decimal hedgeExitPrice = 0m;

        if (trade.IsHedged && trade.HedgeStrike.HasValue)
        {
            OptionCandle? hedgeExit = await _repository.GetOptionCandleAsync(
                date,
                expiryDate,
                trade.HedgeStrike.Value,
                trade.SoldOptionType,
                time,
                cancellationToken);

            if (hedgeExit == null)
            {
                return false;
            }

            hedgeExitPrice = hedgeExit.Open;
            trade.HedgeExitPrice = hedgeExit.Open;
        }

        trade.ExitTime = time;
        trade.SoldExitPrice = soldExit.Open;
        trade.ExitReason = reason;
        trade.GrossPnl = CalculateGrossPnl(
            trade,
            soldExit.Open,
            hedgeExitPrice);

        trade.Costs = trade.IsHedged
            ? _options.HedgedRoundTripCost
            : _options.NakedRoundTripCost;

        trade.NetPnl = trade.GrossPnl - trade.Costs;

        if (trade.GrossPnl > trade.BestMarkPnl)
        {
            trade.BestMarkPnl = trade.GrossPnl;
        }

        if (trade.GrossPnl < trade.WorstMarkPnl)
        {
            trade.WorstMarkPnl = trade.GrossPnl;
        }

        result.Trades.Add(trade);
        return true;
    }

    private bool CanEnterAt(TimeOnly indiaTime)
    {
        TimeOnly start = TimeOnly.FromTimeSpan(_options.EntryStart);
        TimeOnly end = TimeOnly.FromTimeSpan(_options.LastEntry);

        return indiaTime >= start && indiaTime <= end;
    }

    private async Task<Dictionary<decimal, List<OptionCandle>>> LoadContractsAsync(
        DateOnly date,
        DateOnly expiryDate,
        IEnumerable<decimal> strikes,
        string optionType,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<decimal, List<OptionCandle>>();

        foreach (decimal strike in strikes)
        {
            List<OptionCandle> candles = await _repository.GetOptionCandlesAsync(
                date,
                expiryDate,
                strike,
                optionType,
                cancellationToken);

            result[strike] = candles;
        }

        return result;
    }

    private static List<DateTimeOffset> BuildReplayTimeline(
        DateOnly date,
        IEnumerable<SignalPairDefinition> strikes,
        IReadOnlyDictionary<decimal, List<OptionCandle>> calls,
        IReadOnlyDictionary<decimal, List<OptionCandle>> puts)
    {
        if (!strikes.Any(p=>calls[p.CallStrike].Any(c=>puts[p.PutStrike].Any(v=>v.CandleTime==c.CandleTime)))) return [];

        // Replay uses the official India market clock, not an intersection of
        // every strike. A single missing option bar must not truncate the whole
        // day. Individual missing CE/PE bars are represented as null in
        // BuildPairsAtTime and simply cannot contribute to that candle's signal.
        DateTimeOffset cursor = MarketClock.ToUtc(
            date,
            new TimeOnly(9, 15));

        DateTimeOffset end = MarketClock.ToUtc(
            date,
            new TimeOnly(15, 30));

        var timeline = new List<DateTimeOffset>();

        while (cursor <= end)
        {
            timeline.Add(cursor);
            cursor = cursor.AddMinutes(5);
        }

        return timeline;
    }

    private static List<CandlePair> BuildPairsAtTime(
        IEnumerable<SignalPairDefinition> strikes,
        IReadOnlyDictionary<decimal, List<OptionCandle>> calls,
        IReadOnlyDictionary<decimal, List<OptionCandle>> puts,
        DateTimeOffset time)
    {
        return PremiumSignalService.BuildPairs(strikes,calls,puts,time);
    }

    private static void ValidatePositionSize(int lots, int lotSize)
    {
        if (lots <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lots),
                "Lots must be greater than zero.");
        }

        if (lotSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lotSize),
                "Lot size must be greater than zero.");
        }
    }

    public static decimal CalculateGrossPnl(
        StrategyTrade trade,
        decimal soldCurrentPrice,
        decimal hedgeCurrentPrice)
    {
        decimal shortPnl = (trade.EntryPrice - soldCurrentPrice) * trade.Quantity;

        if (!trade.IsHedged || !trade.HedgeEntryPrice.HasValue)
        {
            return shortPnl;
        }

        decimal hedgePnl = (hedgeCurrentPrice - trade.HedgeEntryPrice.Value) *
            trade.Quantity;

        return shortPnl + hedgePnl;
    }
}
