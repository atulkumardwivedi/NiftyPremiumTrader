using NiftyPremiumTrader.Api.Models;

namespace NiftyPremiumTrader.Api.Data;

public interface ICandleStore
{
    void ConfigureLegacySource(string schema,bool timesAreUtc);
    Task<MorningSelection?> GetMorningSelectionAsync(DateOnly date,CancellationToken ct);
    Task<MorningSelection> SaveMorningSelectionAsync(MorningSelection selection,CancellationToken ct);
    Task<DateOnly?> ResolveOpstraExpiryAsync(DateOnly date,CancellationToken ct);
    Task<DateOnly?> ResolveExpiryAsync(DateOnly date,CancellationToken ct);
    Task AddExpiryAsync(DateOnly expiry,CancellationToken ct);
    Task<decimal?> GetMorningSpotAsync(DateOnly date,CancellationToken ct);
    Task PopulateLegacyDeltasAsync(List<OptionCandle> candles,CancellationToken ct);
    Task<List<OptionCandle>> GetIndexCandlesAsync(DateOnly date,CancellationToken ct);
    Task<List<OptionCandle>> GetOptionCandlesAsync(DateOnly date,DateOnly expiry,decimal strike,string type,CancellationToken ct);
    Task<OptionCandle?> GetOptionCandleAsync(DateOnly date,DateOnly expiry,decimal strike,string type,DateTimeOffset time,CancellationToken ct);
    Task UpsertCandlesAsync(IReadOnlyCollection<OptionCandle> candles,CancellationToken ct);
    Task MarkHistoricalDayLoadedAsync(DateOnly date,string loadKey,int count,CancellationToken ct);
}
