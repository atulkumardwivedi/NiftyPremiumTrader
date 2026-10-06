using NiftyPremiumTrader.Api.Models;

namespace NiftyPremiumTrader.Api.Services;

public interface IBreezeDaySource
{
    Task<List<OptionCandle>> GetOptionDayAsync(DateOnly date,DateOnly expiry,decimal strike,string optionType,bool live,CancellationToken ct);
    Task<List<OptionCandle>> GetIndexDayAsync(DateOnly date,bool live,CancellationToken ct);
}
