using Microsoft.Extensions.Options;
using NiftyPremiumTrader.Api.Data;
using NiftyPremiumTrader.Api.Models;
using NiftyPremiumTrader.Api.Options;

namespace NiftyPremiumTrader.Api.Services;

public sealed class LiveRuntimeState
{
    public bool Running { get; set; }
    public bool ExecuteBrokerOrders { get; set; }
    public string MarketDataProvider { get; set; } = "ICICI_BREEZE";
    public string ExecutionBroker { get; set; } = "KOTAK_NEO";
    public DateOnly SessionDate { get; set; }
    public DateOnly ExpiryDate { get; set; }
    public int HedgeDistance { get; set; }
    public int Lots { get; set; }
    public int LotSize { get; set; }
    public int Quantity => Lots * LotSize;
    public bool AutoLots { get; set; }
    public string SetupProfile { get; set; } = "ScaledWinner";
    public RiskSetup? Setup { get; set; }
    public SizingPreview? Margin { get; set; }
    public int RefreshMinutes { get; set; } = 5;
    public DateTimeOffset ServerIndiaTime { get; set; }
    public DateTimeOffset? LastRefreshBoundary { get; set; }
    public DateTimeOffset? ExpectedCompletedCandle { get; set; }
    public DateTimeOffset? LastProcessedCandle { get; set; }
    public PremiumSignal? LastSignal { get; set; }
    public StrategyTrade? ActiveTrade { get; set; }
    public SignalDirection PreviousDirection { get; set; }
    public SignalDirection LastTakenDirection { get; set; }
    public bool SawNeutralSinceLastTrade { get; set; } = true;
    public int CooldownRemaining { get; set; }
    public string EntryStatus { get; set; } = "STOPPED";
    public string Message { get; set; } = "Stopped";
    public LiveBudgetPlan? BudgetPlan { get; set; }
}

public sealed class LiveFillResult
{
    public decimal Price { get; set; }
    public string OrderId { get; set; } = string.Empty;
}

public sealed class LiveTradingService
{
    private readonly PositionSizingService _sizing;
    private readonly CandleRepository _repository;
    private readonly TradeRepository _tradeRepository;
    private readonly CandleDataService _dataService;
    private readonly PremiumSignalService _signalService;
    private readonly KotakOrderService _kotakOrderService;
    private readonly ApiBudgetService _budgetService;
    private readonly StrategyOptions _strategyOptions;
    private readonly LiveTradingOptions _liveOptions;
    private readonly object _stateLock = new();

    private LiveRuntimeState _state = new();

    public LiveTradingService(
        PositionSizingService sizing,
        CandleRepository repository,
        TradeRepository tradeRepository,
        CandleDataService dataService,
        PremiumSignalService signalService,
        KotakOrderService kotakOrderService,
        ApiBudgetService budgetService,
        IOptions<StrategyOptions> strategyOptions,
        IOptions<LiveTradingOptions> liveOptions)
    {
        _sizing = sizing;
        _repository = repository;
        _tradeRepository = tradeRepository;
        _dataService = dataService;
        _signalService = signalService;
        _kotakOrderService = kotakOrderService;
        _budgetService = budgetService;
        _strategyOptions = strategyOptions.Value;
        _liveOptions = liveOptions.Value;
    }

    public async Task<LiveRuntimeState> StartAsync(
        DateOnly? requestedExpiry,
        int hedgeDistance,
        int lots,
        int lotSize,
        bool executeBrokerOrders,
        CancellationToken cancellationToken,
        bool autoLots = false, string setupProfile = "ScaledWinner")
    {
        lock (_stateLock) { if (_state.Running) throw new InvalidOperationException("Stop the running session before changing lot settings."); }
        if (!_liveOptions.Enabled)
        {
            throw new InvalidOperationException(
                "Live trading is disabled in appsettings.json.");
        }

        if (executeBrokerOrders && !_liveOptions.AllowBrokerOrders)
        {
            throw new InvalidOperationException(
                "The UI requested real Kotak orders, but LiveTrading:AllowBrokerOrders is false.");
        }

        if (executeBrokerOrders)
        {
            await _kotakOrderService.EnsureReadyAsync(
                cancellationToken);
        }

        DateOnly today = MarketClock.IndiaDateNow();

        DateOnly expiry = await _dataService.ResolveExpiryAsync(
            today,
            requestedExpiry,
            cancellationToken);

        _dataService.ValidateHedgeDistance(hedgeDistance);
        ValidatePositionSize(lots, lotSize);

        List<StrategyTrade> openTrades = await _tradeRepository.GetOpenTradesAsync(
            today,
            executeBrokerOrders,
            cancellationToken);

        if (openTrades.Count > 1)
        {
            throw new InvalidOperationException(
                "More than one open live trade exists in the database. " +
                "Live trading is blocked until the duplicate open trades are reconciled.");
        }

        StrategyTrade? restoredTrade = openTrades.SingleOrDefault();
        SizingPreview? sizing = null;
        RiskSetup setup;
        if (restoredTrade != null)
        {
            lots=restoredTrade.Lots;lotSize=restoredTrade.LotSize;
            setup=restoredTrade.RiskSetup ?? new RiskSetup(lots,_strategyOptions.CooldownCandles,
                _strategyOptions.ProfitTarget,_strategyOptions.HedgeTriggerLoss,
                _strategyOptions.NakedCatastrophicStop,_strategyOptions.HedgedStop,"LegacyFixed","Restored original fixed thresholds");
            restoredTrade.RiskSetup=setup;
            await _tradeRepository.SaveLiveTradeAsync(restoredTrade,cancellationToken);
        }
        else if (executeBrokerOrders || autoLots)
        {
            sizing=await _sizing.PreviewAsync(expiry,lots,autoLots,setupProfile,cancellationToken);
            if(!sizing.Affordable)throw new InvalidOperationException(sizing.Message);
            lots=sizing.Lots;lotSize=sizing.LotSize;setup=sizing.Setup!;
        }
        else setup=_sizing.GetSetup(lots,lotSize,setupProfile);


        // ICICI live refresh uses one NIFTY cash call, four CE calls,
        // four PE calls. A hedge must already be one of those contracts.
        const int maximumContractsPerRefresh = 9;

        LiveBudgetPlan plan = await _budgetService.BuildLivePlanAsync(
            maximumContractsPerRefresh,
            TimeOnly.FromTimeSpan(_strategyOptions.EndOfDayExit),
            cancellationToken);

        // Resolve the opening ATM once. A transient empty ICICI response is
        // treated as DATA NOT READY; the background worker will retry instead
        // of failing Start Live.
        var initialSelection = await _dataService.EnsureMorningSelectionAsync(today,expiry,true,cancellationToken);
        bool marketDataReady = initialSelection != null;

        var state = new LiveRuntimeState
        {
            Running = true,
            ExecuteBrokerOrders = executeBrokerOrders,
            MarketDataProvider = "ICICI_BREEZE",
            ExecutionBroker = "KOTAK_NEO",
            SessionDate = today,
            ExpiryDate = expiry,
            HedgeDistance = hedgeDistance,
            Lots = lots,
            LotSize = lotSize,
            AutoLots = autoLots, SetupProfile = setupProfile, Setup = setup, Margin = sizing,
            RefreshMinutes = plan.RefreshMinutes,
            ServerIndiaTime = MarketClock.ToIndia(DateTimeOffset.UtcNow),
            ActiveTrade = restoredTrade,
            LastTakenDirection = restoredTrade?.Direction ?? SignalDirection.None,
            SawNeutralSinceLastTrade = restoredTrade == null,
            EntryStatus = restoredTrade != null
                ? "BLOCKED - OPEN TRADE"
                : marketDataReady
                    ? "WAITING FOR SIGNAL"
                    : "WAITING FOR ICICI NIFTY DATA",
            Message = restoredTrade != null
                ? $"Restored open trade SELL {restoredTrade.SoldStrike:0} {restoredTrade.SoldOptionType} from database."
                : marketDataReady
                    ? BuildStartMessage(executeBrokerOrders, plan)
                    : "Live mode started. Waiting for ICICI NIFTY candles; the worker will retry automatically.",
            BudgetPlan = plan
        };

        lock (_stateLock)
        {
            _state = state;
        }

        return GetState();
    }

    public LiveRuntimeState Stop()
    {
        lock (_stateLock)
        {
            _state.Running = false;
            _state.EntryStatus = "STOPPED";
            _state.Message = "Stopped";
            return _state;
        }
    }

    public void NotifyManualBrokerPositionExit(
        string tradingSymbol,
        string source)
    {
        if (!source.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase) &&
            !source.Equals("MIXED", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (_stateLock)
        {
            _state.Running = false;
            _state.EntryStatus = "STOPPED - MANUAL BROKER EXIT";
            _state.Message =
                $"Manual broker exit was sent for {tradingSymbol}. " +
                "Automatic strategy trading was stopped to prevent a duplicate strategy exit. " +
                "Refresh Kotak positions and reconcile the strategy before restarting live trading.";
        }
    }

    public LiveRuntimeState GetState()
    {
        lock (_stateLock)
        {
            return _state;
        }
    }

    public void HandleWorkerError(Exception exception)
    {
        lock (_stateLock)
        {
            _state.Message = "Live worker error: " + exception.Message;

            if (_state.ExecuteBrokerOrders)
            {
                _state.Running = false;
                _state.Message +=
                    " Real-order mode was stopped. Verify broker positions/orders before restarting.";
            }
        }
    }

    public async Task ProcessAsync(CancellationToken cancellationToken)
    {
        LiveRuntimeState state = GetState();

        if (!state.Running)
        {
            return;
        }

        DateTimeOffset indiaNow = MarketClock.ToIndia(DateTimeOffset.UtcNow);
        DateOnly today = DateOnly.FromDateTime(indiaNow.DateTime);
        TimeOnly now = TimeOnly.FromDateTime(indiaNow.DateTime);

        state.ServerIndiaTime = indiaNow;

        if (today != state.SessionDate)
        {
            Stop();
            return;
        }

        TimeOnly eod = TimeOnly.FromTimeSpan(_strategyOptions.EndOfDayExit);

        if (state.ActiveTrade != null && now >= eod)
        {
            if(!state.ExecuteBrokerOrders) await _dataService.EnsureSignalContractsAsync(today,state.ExpiryDate,true,cancellationToken);
            await ExitLiveTradeAsync(
                state,
                "EOD",
                cancellationToken);

            return;
        }

        if (now < new TimeOnly(9, 20) || now > eod)
        {
            return;
        }

        DateTimeOffset refreshBoundary = GetCurrentFiveMinuteBoundaryUtc();

        if (!ShouldRefresh(state, refreshBoundary))
        {
            return;
        }

        var morningSelection=await _dataService.EnsureMorningSelectionAsync(today,state.ExpiryDate,true,cancellationToken);
        List<SignalPairDefinition> strikes=morningSelection?.Pairs ?? [];

        if (strikes.Count == 0)
        {
            state.EntryStatus = "WAITING FOR ICICI NIFTY DATA";
            state.Message =
                "ICICI returned no usable NIFTY candle yet. Live mode is still running and will retry this boundary.";
            return;
        }

        await _dataService.EnsureSignalContractsAsync(
            today,
            state.ExpiryDate,
            true,
            cancellationToken);

        // Mark the boundary only after ICICI has refreshed the complete
        // signal set successfully. If ICICI fails temporarily, the worker
        // can retry this same 5-minute boundary on the next poll.
        // Commit boundary only when the latest completed eight-contract snapshot exists.

        DateTimeOffset? latestCompletedCandle =
            MarketClock.LatestCompletedFiveMinuteCandleStartUtc();

        state.ExpectedCompletedCandle = latestCompletedCandle;

        if (!latestCompletedCandle.HasValue)
        {
            state.EntryStatus = "WAITING FOR FIRST COMPLETED CANDLE";
            state.Message = "Waiting for the first completed 5-minute candle.";
            return;
        }

        Dictionary<decimal, List<OptionCandle>> calls = await LoadContractsAsync(
            today,
            state.ExpiryDate,
            strikes.Select(p=>p.CallStrike),
            "CE",
            latestCompletedCandle.Value,
            cancellationToken);

        Dictionary<decimal, List<OptionCandle>> puts = await LoadContractsAsync(
            today,
            state.ExpiryDate,
            strikes.Select(p=>p.PutStrike),
            "PE",
            latestCompletedCandle.Value,
            cancellationToken);

        DateTimeOffset? latestCommonTime = FindLatestCommonTime(
            strikes,
            calls,
            puts);

        if (!latestCommonTime.HasValue || latestCommonTime.Value != latestCompletedCandle.Value)
        {
            state.EntryStatus = "WAITING FOR COMPLETE CE/PE SET";
            state.Message = "Waiting for a complete CE/PE candle set.";
            return;
        }

        if (!state.ExecuteBrokerOrders)
        {
            foreach (var pair in strikes)
            {
                if (await _dataService.TryGetLiveOptionPriceAsync(today,state.ExpiryDate,pair.CallStrike,"CE",cancellationToken).ConfigureAwait(false) is not decimal ||
                    await _dataService.TryGetLiveOptionPriceAsync(today,state.ExpiryDate,pair.PutStrike,"PE",cancellationToken).ConfigureAwait(false) is not decimal)
                {
                    state.EntryStatus = "WAITING FOR CURRENT CANDLE OPEN";
                    return;
                }
            }
        }
        state.LastRefreshBoundary = refreshBoundary;
        if (state.LastProcessedCandle.HasValue &&
            state.LastProcessedCandle.Value >= latestCommonTime.Value)
        {
            return;
        }

        DateTimeOffset candleTime = latestCommonTime.Value;

        List<CandlePair> pairs = BuildPairs(
            strikes,
            calls,
            puts,
            candleTime);

        PremiumSignal signal = _signalService.Evaluate(
            candleTime,
            pairs);

        // On restart, compare against the immediately preceding completed candle.
        SignalDirection previous = state.LastProcessedCandle.HasValue ? state.PreviousDirection :
            _signalService.Evaluate(candleTime.AddMinutes(-5),BuildPairs(strikes,calls,puts,candleTime.AddMinutes(-5))).Direction;
        bool freshDirection=signal.Direction!=previous;
        state.PreviousDirection=signal.Direction;
        state.LastProcessedCandle = candleTime;
        state.LastSignal = signal;

        if (signal.Direction == SignalDirection.None)
        {
            state.SawNeutralSinceLastTrade = true;
        }

        if (state.ActiveTrade != null)
        {
            state.EntryStatus = "BLOCKED - OPEN TRADE";

            await ManageLiveTradeAsync(
                state,
                candleTime,
                cancellationToken);

            return;
        }

        if (state.CooldownRemaining > 0)
        {
            state.EntryStatus =
                $"COOLDOWN - {state.CooldownRemaining} CANDLE(S) REMAINING";
            state.CooldownRemaining--;
            return;
        }

        TimeOnly candleIndiaTime = TimeOnly.FromDateTime(
            MarketClock.ToIndia(candleTime).DateTime);

        if (!CanEnterAt(candleIndiaTime))
        {
            state.EntryStatus = "OUTSIDE ENTRY WINDOW";
            return;
        }

        if (signal.Direction == SignalDirection.None)
        {
            state.EntryStatus = "WAITING FOR ENTRY SIGNAL";
            return;
        }

        if (!freshDirection || !signal.StrengthFilterPassed)
        {
            state.EntryStatus = "WAITING FOR FRESH SIGNAL";
            return;
        }

        string soldOptionType = signal.Direction == SignalDirection.Bullish
            ? "PE"
            : "CE";

        decimal soldStrike = signal.SelectedAtmStrike;

        if (soldStrike <= 0m)
        {
            state.EntryStatus = "NO VALID SHORT STRIKE";
            return;
        }

        List<StrategyTrade> unexpectedOpenTrades =
            await _tradeRepository.GetOpenTradesAsync(
                today,
                state.ExecuteBrokerOrders,
                cancellationToken);

        if (unexpectedOpenTrades.Count > 0)
        {
            state.ActiveTrade = unexpectedOpenTrades[0];
            state.EntryStatus = "BLOCKED - OPEN TRADE";
            state.Message =
                "An open trade was found in the database and restored. No overlapping trade was opened.";
            return;
        }

        if (state.ExecuteBrokerOrders)
        {
            var check=await _sizing.PreviewAsync(state.ExpiryDate,state.Lots,state.AutoLots,state.SetupProfile,
                cancellationToken,soldStrike,soldOptionType);
            state.Margin=check;
            if(!check.Affordable){state.EntryStatus="BLOCKED - MARGIN";state.Message=check.Message;return;}
            state.Lots=check.Lots;state.LotSize=check.LotSize;state.Setup=check.Setup;
        }
        if (!state.Running) return;
        LiveFillResult entry = await ResolveLiveFillAsync(
            state,
            state.ExpiryDate,
            soldStrike,
            soldOptionType,
            "sell",
            state.Quantity,
            cancellationToken);

        var trade = new StrategyTrade
        {
            SessionDate = today,
            RiskSetup = state.Setup,
            Direction = signal.Direction,
            SignalTime = signal.CandleTime,
            SoldOptionType = soldOptionType,
            SoldStrike = soldStrike,
            EntryTime = DateTimeOffset.UtcNow,
            EntryPrice = entry.Price,
            Lots = state.Lots,
            LotSize = state.LotSize,
            Quantity = state.Quantity,
            HedgeDistance = state.HedgeDistance,
            SoldEntryOrderId = entry.OrderId,
            BrokerExecution = state.ExecuteBrokerOrders,
            BestMarkPnl = 0m,
            WorstMarkPnl = 0m
        };

        state.ActiveTrade = trade;
        state.LastTakenDirection = signal.Direction;
        state.SawNeutralSinceLastTrade = false;
        state.EntryStatus = "BLOCKED - OPEN TRADE";
        state.Message =
            $"Entered SELL {soldStrike:0} {soldOptionType} @ {entry.Price:0.00}";

        await _tradeRepository.SaveLiveTradeAsync(
            trade,
            cancellationToken);
    }

    private async Task ManageLiveTradeAsync(
        LiveRuntimeState state,
        DateTimeOffset candleTime,
        CancellationToken cancellationToken)
    {
        StrategyTrade trade = state.ActiveTrade!;

        OptionCandle? sold = await _repository.GetCandleAtOrBeforeAsync(
            state.SessionDate,
            state.ExpiryDate,
            trade.SoldStrike,
            trade.SoldOptionType,
            candleTime,
            cancellationToken);

        if (sold == null)
        {
            return;
        }

        decimal hedgeCurrentPrice = 0m;

        if (trade.IsHedged && trade.HedgeStrike.HasValue)
        {
            await _dataService.EnsureContractAsync(
                state.SessionDate,
                state.ExpiryDate,
                trade.HedgeStrike.Value,
                trade.SoldOptionType,
                true,
                cancellationToken);

            OptionCandle? hedge = await _repository.GetCandleAtOrBeforeAsync(
                state.SessionDate,
                state.ExpiryDate,
                trade.HedgeStrike.Value,
                trade.SoldOptionType,
                candleTime,
                cancellationToken);

            if (hedge != null)
            {
                hedgeCurrentPrice = hedge.Close;
            }
            else
            {
                decimal? fallbackPrice =
                    await _dataService.TryGetLiveOptionPriceAsync(
                        state.SessionDate,
                        state.ExpiryDate,
                        trade.HedgeStrike.Value,
                        trade.SoldOptionType,
                        cancellationToken);

                if (!fallbackPrice.HasValue)
                {
                    state.Message =
                        "Open trade is active, but the current ICICI hedge price is not available yet.";
                    return;
                }

                hedgeCurrentPrice = fallbackPrice.Value;
            }
        }

        decimal markPnl = StrategyEngine.CalculateGrossPnl(
            trade,
            sold.Close,
            hedgeCurrentPrice);

        UpdateTradeMark(trade, markPnl);

        if (markPnl >= (trade.RiskSetup?.ProfitTarget ?? _strategyOptions.ProfitTarget))
        {
            await ExitLiveTradeAsync(
                state,
                "TARGET",
                cancellationToken);

            return;
        }

        if (!trade.IsHedged &&
            markPnl <= (trade.RiskSetup?.NakedCatastrophicStop ?? _strategyOptions.NakedCatastrophicStop))
        {
            await ExitLiveTradeAsync(
                state,
                "NAKED_STOP",
                cancellationToken);

            return;
        }

        if (!trade.IsHedged &&
            markPnl <= (trade.RiskSetup?.HedgeTriggerLoss ?? _strategyOptions.HedgeTriggerLoss))
        {
            await EnterLiveHedgeAsync(
                state,
                trade,
                markPnl,
                cancellationToken);

            return;
        }

        if (trade.IsHedged &&
            markPnl <= (trade.RiskSetup?.HedgedStop ?? _strategyOptions.HedgedStop))
        {
            await ExitLiveTradeAsync(
                state,
                "HEDGED_STOP",
                cancellationToken);

            return;
        }

        await _tradeRepository.SaveLiveTradeAsync(
            trade,
            cancellationToken);
    }

    private async Task EnterLiveHedgeAsync(
        LiveRuntimeState state,
        StrategyTrade trade,
        decimal triggerPnl,
        CancellationToken cancellationToken)
    {
        decimal hedgeStrike = _dataService.GetHedgeStrike(
            trade.SoldStrike,
            trade.SoldOptionType,
            state.HedgeDistance);

        if (!await _dataService.IsSelectedContractAsync(state.SessionDate,hedgeStrike,trade.SoldOptionType,cancellationToken))
        {
            state.Message=$"300-point hedge {hedgeStrike} {trade.SoldOptionType} is outside the fixed morning set; naked stop remains active.";
            return;
        }
        LiveFillResult hedge = await ResolveLiveFillAsync(
            state,
            state.ExpiryDate,
            hedgeStrike,
            trade.SoldOptionType,
            "buy",
            trade.Quantity,
            cancellationToken);

        trade.HedgeStrike = hedgeStrike;
        trade.HedgeEntryTime = DateTimeOffset.UtcNow;
        trade.HedgeEntryPrice = hedge.Price;
        trade.HedgeTriggerPnl = triggerPnl;
        trade.HedgeEntryOrderId = hedge.OrderId;

        state.Message =
            $"Hedge BUY {hedgeStrike:0} {trade.SoldOptionType} @ {hedge.Price:0.00}";

        await _tradeRepository.SaveLiveTradeAsync(
            trade,
            cancellationToken);
    }

    private async Task ExitLiveTradeAsync(
        LiveRuntimeState state,
        string reason,
        CancellationToken cancellationToken)
    {
        StrategyTrade trade = state.ActiveTrade!;

        LiveFillResult soldExit = await ResolveLiveFillAsync(
            state,
            state.ExpiryDate,
            trade.SoldStrike,
            trade.SoldOptionType,
            "buy",
            trade.Quantity,
            cancellationToken);

        decimal hedgeExitPrice = 0m;

        if (trade.IsHedged && trade.HedgeStrike.HasValue)
        {
            LiveFillResult hedgeExit = await ResolveLiveFillAsync(
                state,
                state.ExpiryDate,
                trade.HedgeStrike.Value,
                trade.SoldOptionType,
                "sell",
                trade.Quantity,
                cancellationToken);

            hedgeExitPrice = hedgeExit.Price;
            trade.HedgeExitPrice = hedgeExit.Price;
            trade.HedgeExitOrderId = hedgeExit.OrderId;
        }

        trade.ExitTime = DateTimeOffset.UtcNow;
        trade.SoldExitPrice = soldExit.Price;
        trade.SoldExitOrderId = soldExit.OrderId;
        trade.ExitReason = reason;

        trade.GrossPnl = StrategyEngine.CalculateGrossPnl(
            trade,
            soldExit.Price,
            hedgeExitPrice);

        trade.Costs = trade.IsHedged
            ? _strategyOptions.HedgedRoundTripCost
            : _strategyOptions.NakedRoundTripCost;

        trade.NetPnl = trade.GrossPnl - trade.Costs;

        if (trade.GrossPnl > trade.BestMarkPnl)
        {
            trade.BestMarkPnl = trade.GrossPnl;
        }

        if (trade.GrossPnl < trade.WorstMarkPnl)
        {
            trade.WorstMarkPnl = trade.GrossPnl;
        }

        await _tradeRepository.SaveLiveTradeAsync(
            trade,
            cancellationToken);

        state.Message =
            $"Exit {reason}. Net P&L {trade.NetPnl:0}";

        state.ActiveTrade = null;
        state.CooldownRemaining = trade.RiskSetup?.CooldownCandles ?? _strategyOptions.CooldownCandles;
        state.EntryStatus =
            $"COOLDOWN - {state.CooldownRemaining} CANDLE(S)";
    }

    private async Task<LiveFillResult> ResolveLiveFillAsync(
        LiveRuntimeState state,
        DateOnly expiryDate,
        decimal strike,
        string optionType,
        string action,
        int quantity,
        CancellationToken cancellationToken)
    {
        if (state.ExecuteBrokerOrders)
        {
            string tag = BuildKotakOrderTag(
                action,
                strike,
                optionType);

            KotakOrderExecutionResult order =
                await _kotakOrderService.PlaceOptionMarketOrderAsync(
                    expiryDate,
                    strike,
                    optionType,
                    action,
                    quantity,
                    state.LotSize,
                    tag,
                    cancellationToken);

            return new LiveFillResult
            {
                Price = order.FillPrice,
                OrderId = order.OrderId
            };
        }

        decimal? iciciPrice = await _dataService.TryGetLiveOptionPriceAsync(
            state.SessionDate,
            expiryDate,
            strike,
            optionType,
            cancellationToken);

        if (!iciciPrice.HasValue)
        {
            throw new InvalidOperationException(
                $"No current ICICI price is available for {strike:0.##} {optionType}.");
        }

        return new LiveFillResult
        {
            Price = iciciPrice.Value,
            OrderId = string.Empty
        };
    }

    private static string BuildKotakOrderTag(
        string action,
        decimal strike,
        string optionType)
    {
        string side = action.Equals(
            "sell",
            StringComparison.OrdinalIgnoreCase)
            ? "S"
            : "B";

        return $"NPT:{side}:{strike:0}:{optionType}";
    }

    private void UpdateTradeMark(StrategyTrade trade, decimal markPnl)
    {
        trade.GrossPnl = markPnl;
        trade.Costs = trade.IsHedged
            ? _strategyOptions.HedgedRoundTripCost
            : _strategyOptions.NakedRoundTripCost;
        trade.NetPnl = markPnl - trade.Costs;

        if (markPnl > trade.BestMarkPnl)
        {
            trade.BestMarkPnl = markPnl;
        }

        if (markPnl < trade.WorstMarkPnl)
        {
            trade.WorstMarkPnl = markPnl;
        }
    }

    private bool CanEnterAt(TimeOnly candleIndiaTime)
    {
        TimeOnly start = TimeOnly.FromTimeSpan(_strategyOptions.EntryStart);
        TimeOnly end = TimeOnly.FromTimeSpan(_strategyOptions.LastEntry);

        return candleIndiaTime >= start && candleIndiaTime <= end;
    }

    private bool ShouldRefresh(
        LiveRuntimeState state,
        DateTimeOffset boundary)
    {
        if (!state.LastRefreshBoundary.HasValue)
        {
            return true;
        }

        double minutes = (boundary - state.LastRefreshBoundary.Value).TotalMinutes;
        return minutes >= state.RefreshMinutes;
    }

    private static DateTimeOffset GetCurrentFiveMinuteBoundaryUtc()
    {
        DateTimeOffset indiaNow = MarketClock.ToIndia(DateTimeOffset.UtcNow);
        int minute = indiaNow.Minute - indiaNow.Minute % 5;

        DateTime indiaBoundary = new(
            indiaNow.Year,
            indiaNow.Month,
            indiaNow.Day,
            indiaNow.Hour,
            minute,
            0,
            DateTimeKind.Unspecified);

        return MarketClock.ToUtc(indiaBoundary);
    }

    private async Task<Dictionary<decimal, List<OptionCandle>>> LoadContractsAsync(
        DateOnly date,
        DateOnly expiryDate,
        IEnumerable<decimal> strikes,
        string optionType,
        DateTimeOffset liveCutoff,
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

            // Never let live mode read a candle later than the latest
            // completed five-minute candle, even if stale future rows exist
            // in PostgreSQL from a previous build/test.
            result[strike] = candles
                .Where(x => x.IsComplete && x.CandleTime <= liveCutoff)
                .OrderBy(x => x.CandleTime)
                .ToList();
        }

        return result;
    }

    private static DateTimeOffset? FindLatestCommonTime(
        IEnumerable<SignalPairDefinition> strikes,
        IReadOnlyDictionary<decimal, List<OptionCandle>> calls,
        IReadOnlyDictionary<decimal, List<OptionCandle>> puts)
    {
        HashSet<DateTimeOffset>? common = null;

        foreach (var pair in strikes)
        {
            var times = calls[pair.CallStrike]
                .Select(x => x.CandleTime)
                .Intersect(puts[pair.PutStrike].Select(x => x.CandleTime))
                .ToHashSet();

            if (common == null)
            {
                common = times;
            }
            else
            {
                common.IntersectWith(times);
            }
        }

        if (common == null || common.Count == 0)
        {
            return null;
        }

        return common.Max();
    }

    private static List<CandlePair> BuildPairs(
        IEnumerable<SignalPairDefinition> strikes,
        IReadOnlyDictionary<decimal, List<OptionCandle>> calls,
        IReadOnlyDictionary<decimal, List<OptionCandle>> puts,
        DateTimeOffset time)
    {
        return PremiumSignalService.BuildPairs(strikes,calls,puts,time);
    }

    private static string BuildStartMessage(
        bool executeBrokerOrders,
        LiveBudgetPlan plan)
    {
        string mode = executeBrokerOrders
            ? "LIVE BROKER MODE"
            : "LIVE PAPER MODE";

        if (!plan.IsDegraded)
        {
            return $"{mode} - ICICI data / Kotak execution - 5 minute refresh";
        }

        return $"{mode} - ICICI data / Kotak execution - " +
            $"API budget degraded refresh to {plan.RefreshMinutes} minutes";
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

}
