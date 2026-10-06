using Microsoft.Extensions.Options;
using NiftyPremiumTrader.Api.Data;
using NiftyPremiumTrader.Api.Models;
using NiftyPremiumTrader.Api.Options;

namespace NiftyPremiumTrader.Api.Services;

public sealed class ApiBudgetExceededException : Exception
{
    public ApiBudgetExceededException(string message)
        : base(message)
    {
    }
}

public sealed class ApiBudgetService
{
    private readonly CandleRepository _repository;
    private readonly ApiBudgetOptions _options;

    public ApiBudgetService(
        CandleRepository repository,
        IOptions<ApiBudgetOptions> options)
    {
        _repository = repository;
        _options = options.Value;
    }

    public async Task ConsumeAsync(
        int count,
        bool livePriority,
        CancellationToken cancellationToken)
    {
        DateOnly today = MarketClock.IndiaDateNow();

        int ceiling = livePriority
            ? _options.DailyLimit
            : _options.DailyLimit - _options.ReservedForLive;

        bool consumed = await _repository.TryConsumeApiCallsAsync(
            today,
            count,
            ceiling,
            cancellationToken);

        if (consumed)
        {
            return;
        }

        int used = await _repository.GetUsedApiCallsAsync(
            today,
            cancellationToken);

        throw new ApiBudgetExceededException(
            $"ICICI API budget blocked this call. Used {used} of {_options.DailyLimit}. " +
            $"Historical loading preserves {_options.ReservedForLive} calls for live trading.");
    }

    public async Task<ApiBudgetStatus> GetStatusAsync(
        CancellationToken cancellationToken)
    {
        DateOnly today = MarketClock.IndiaDateNow();
        int used = await _repository.GetUsedApiCallsAsync(today, cancellationToken);
        int remaining = Math.Max(0, _options.DailyLimit - used);
        int historicalCeiling = _options.DailyLimit - _options.ReservedForLive;
        int historicalRemaining = Math.Max(0, historicalCeiling - used);

        return new ApiBudgetStatus
        {
            Date = today,
            Used = used,
            Limit = _options.DailyLimit,
            Remaining = remaining,
            ReservedForLive = _options.ReservedForLive,
            HistoricalRemaining = historicalRemaining
        };
    }

    public async Task<LiveBudgetPlan> BuildLivePlanAsync(
        int contractsPerRefresh,
        TimeOnly endTime,
        CancellationToken cancellationToken)
    {
        ApiBudgetStatus status = await GetStatusAsync(cancellationToken);
        int available = Math.Max(0, status.Remaining - _options.LiveSafetyCalls);
        int remainingFiveMinuteSlots = MarketClock.RemainingFiveMinuteRefreshes(endTime);

        if (remainingFiveMinuteSlots == 0)
        {
            return new LiveBudgetPlan
            {
                RefreshMinutes = 5,
                ContractsPerRefresh = contractsPerRefresh,
                EstimatedRemainingRefreshes = 0,
                EstimatedCallsNeeded = 0,
                AvailableCalls = available
            };
        }

        int maximumRefreshes = contractsPerRefresh == 0
            ? remainingFiveMinuteSlots
            : available / contractsPerRefresh;

        if (maximumRefreshes <= 0)
        {
            throw new ApiBudgetExceededException(
                "Not enough ICICI API budget remains to start live strategy polling.");
        }

        int refreshMinutes = 5;

        if (maximumRefreshes < remainingFiveMinuteSlots)
        {
            double ratio = (double)remainingFiveMinuteSlots / maximumRefreshes;
            int requiredFiveMinuteBlocks = (int)Math.Ceiling(ratio);
            refreshMinutes = Math.Min(30, requiredFiveMinuteBlocks * 5);
        }

        int estimatedRefreshes = (int)Math.Ceiling(
            remainingFiveMinuteSlots * 5.0 / refreshMinutes);

        return new LiveBudgetPlan
        {
            RefreshMinutes = refreshMinutes,
            ContractsPerRefresh = contractsPerRefresh,
            EstimatedRemainingRefreshes = estimatedRefreshes,
            EstimatedCallsNeeded = estimatedRefreshes * contractsPerRefresh,
            AvailableCalls = available
        };
    }
}
