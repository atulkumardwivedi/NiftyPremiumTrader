using System.Collections.Concurrent;
using NiftyPremiumTrader.Api.Models;

namespace NiftyPremiumTrader.Api.Services;

public sealed class BacktestSession
{
    public string? SetupProfile { get; init; }
    public Guid SessionId { get; init; }
    public DateOnly Date { get; init; }
    public DateOnly ExpiryDate { get; init; }
    public int HedgeDistance { get; init; }
    public int Lots { get; init; }
    public int LotSize { get; init; }
    public int CurrentIndex { get; set; }
    public List<DateTimeOffset> Timeline { get; init; } = [];
}

public sealed class BacktestService
{
    private readonly PositionSizingService _sizing;
    private readonly StrategyEngine _engine;
    private readonly CandleDataService _dataService;
    private readonly ConcurrentDictionary<Guid, BacktestSession> _sessions = new();

    public BacktestService(
        PositionSizingService sizing,
        StrategyEngine engine,
        CandleDataService dataService)
    {
        _sizing = sizing;
        _engine = engine;
        _dataService = dataService;
    }

    public async Task<StepBacktestResponse> StartAsync(
        StartBacktestRequest request,
        CancellationToken cancellationToken)
    {
        ValidatePositionSize(
            request.Lots,
            request.LotSize);

        DateOnly expiry = await _dataService.ResolveExpiryAsync(
            request.Date,
            request.ExpiryDate,
            cancellationToken);

        // Load the complete NIFTY day and all 4 CE/PE signal pairs once.
        // Only the eight morning-selected contracts are downloaded.
        // Replay and hedge preview use their saved database candles.
        await _dataService.PrepareBacktestDayAsync(
            request.Date,
            expiry,
            request.HedgeDistance,
            cancellationToken);

        List<DateTimeOffset> timeline = await _engine.GetTimelineAsync(
            request.Date,
            expiry,
            cancellationToken);

        if (timeline.Count == 0)
        {
            throw new InvalidOperationException(
                "No common 5-minute option candles are available for the selected day.");
        }

        var session = new BacktestSession
        {
            SetupProfile = request.SetupProfile,
            SessionId = Guid.NewGuid(),
            Date = request.Date,
            ExpiryDate = expiry,
            HedgeDistance = request.HedgeDistance,
            Lots = request.Lots,
            LotSize = request.LotSize,
            CurrentIndex = 0,
            Timeline = timeline
        };

        _sessions[session.SessionId] = session;

        return await BuildStepAsync(
            session,
            cancellationToken);
    }

    public async Task<StepBacktestResponse> MoveAsync(
        Guid sessionId,
        int direction,
        CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(sessionId, out BacktestSession? session))
        {
            throw new KeyNotFoundException(
                "The backtest replay session was not found. Start the day again.");
        }

        int requestedIndex = session.CurrentIndex + direction;

        session.CurrentIndex = Math.Clamp(
            requestedIndex,
            0,
            session.Timeline.Count - 1);

        // No ICICI calls are made here. All replay data is already in PostgreSQL.
        return await BuildStepAsync(
            session,
            cancellationToken);
    }

    public async Task<RangeBacktestResult> RunRangeAsync(
        RangeBacktestRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ToDate < request.FromDate)
        {
            throw new ArgumentException(
                "ToDate must be greater than or equal to FromDate.");
        }

        _dataService.ValidateHedgeDistance(request.HedgeDistance);
        ValidatePositionSize(request.Lots, request.LotSize);

        var result = new RangeBacktestResult
        {
            FromDate = request.FromDate,
            ToDate = request.ToDate
        };

        DateOnly date = request.FromDate;

        while (date <= request.ToDate)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsWeekend(date))
            {
                date = date.AddDays(1);
                continue;
            }

            try
            {
                DateOnly expiry = await _dataService.ResolveExpiryAsync(
                    date,
                    null,
                    cancellationToken);

                await _dataService.PrepareBacktestDayAsync(
                    date,
                    expiry,
                    request.HedgeDistance,
                    cancellationToken);

                DayBacktestResult day = await _engine.RunDayAsync(
                    date,
                    expiry,
                    request.HedgeDistance,
                    request.Lots,
                    request.LotSize,
                    null,
                    cancellationToken, request.SetupProfile == null ? null : _sizing.GetSetup(request.Lots,request.LotSize,request.SetupProfile));

                result.Days.Add(day);
            }
            catch (ApiBudgetExceededException ex)
            {
                result.Messages.Add(
                    $"Stopped on {date}: {ex.Message}");

                break;
            }
            catch (Exception ex)
            {
                result.Messages.Add(
                    $"Skipped {date}: {ex.Message}");
            }

            date = date.AddDays(1);
        }

        return result;
    }

    private async Task<StepBacktestResponse> BuildStepAsync(
        BacktestSession session,
        CancellationToken cancellationToken)
    {
        DayBacktestResult replay = await _engine.RunDayAsync(
            session.Date,
            session.ExpiryDate,
            session.HedgeDistance,
            session.Lots,
            session.LotSize,
            session.CurrentIndex,
            cancellationToken, session.SetupProfile == null ? null : _sizing.GetSetup(session.Lots,session.LotSize,session.SetupProfile));

        decimal realized = replay.Trades.Sum(x => x.NetPnl);
        decimal unrealized = replay.ActiveTrade?.NetPnl ?? 0m;

        return new StepBacktestResponse
        {
            SessionId = session.SessionId,
            Date = session.Date,
            ExpiryDate = session.ExpiryDate,
            CurrentTime = session.Timeline[session.CurrentIndex],
            CurrentIndex = session.CurrentIndex,
            TotalSteps = session.Timeline.Count,
            Signal = replay.LastSignal,
            PendingAction = replay.PendingAction,
            ActiveTrade = replay.ActiveTrade,
            CompletedTrades = replay.Trades,
            RealizedPnl = realized,
            UnrealizedPnl = unrealized
        };
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

    private static bool IsWeekend(DateOnly date)
    {
        return date.DayOfWeek == DayOfWeek.Saturday ||
            date.DayOfWeek == DayOfWeek.Sunday;
    }
}
