using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using NiftyPremiumTrader.Api.Data;
using NiftyPremiumTrader.Api.Models;
using NiftyPremiumTrader.Api.Options;

namespace NiftyPremiumTrader.Api.Services;

public sealed class CandleDataService
{
    private readonly ICandleStore _repository;
    private readonly IBreezeDaySource _breeze;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastLiveRequest = new();

    public CandleDataService(ICandleStore repository, IBreezeDaySource breeze,
        IOptions<StrategyOptions> strategyOptions, IOptions<BreezeOptions> breezeOptions)
    {
        _repository=repository; _breeze=breeze;
        if (strategyOptions.Value.StrikeStep != 100) throw new InvalidOperationException("The locked strategy requires StrikeStep=100.");
        _repository.ConfigureLegacySource(strategyOptions.Value.LegacySchema, strategyOptions.Value.LegacyTimesAreUtc);
    }

    public async Task<DateOnly> ResolveExpiryAsync(DateOnly date, DateOnly? requestedExpiry, CancellationToken ct)
    {
        var stored=await _repository.GetMorningSelectionAsync(date,ct);
        if (stored != null)
        {
            if (requestedExpiry.HasValue && requestedExpiry != stored.ExpiryDate)
                throw new InvalidOperationException("This day's contracts are already frozen with a different expiry.");
            return stored.ExpiryDate;
        }
        DateOnly? expiry = requestedExpiry ?? await _repository.ResolveOpstraExpiryAsync(date,ct)
            ?? await _repository.ResolveExpiryAsync(date,ct);
        if (!expiry.HasValue || expiry.Value < date) throw new InvalidOperationException(
            "No valid expiry is available in opstraoptiondataYYYY/niftyexpiry. Supply the expiry once.");
        await _repository.AddExpiryAsync(expiry.Value,ct);
        return expiry.Value;
    }

    public async Task<MorningSelection?> EnsureMorningSelectionAsync(DateOnly date, DateOnly expiry, bool live, CancellationToken ct)
    {
        var gate=_locks.GetOrAdd($"selection:{date}",_=>new(1,1));
        await gate.WaitAsync(ct);
        try
        {
            var saved=await _repository.GetMorningSelectionAsync(date,ct);
            if (saved != null)
            {
                if (saved.ExpiryDate != expiry) throw new InvalidOperationException("The morning expiry is already frozen.");
                return saved;
            }
            if (live && DateTimeOffset.UtcNow < MarketClock.ToUtc(date,new TimeOnly(9,15))) return null;
            decimal? spot=await _repository.GetMorningSpotAsync(date,ct);
            string source="OPSTRA_FIRST_STORED";
            if (!spot.HasValue || spot.Value<=0)
            {
                await EnsureIndexAsync(date,live,ct);
                var index=await _repository.GetIndexCandlesAsync(date,ct);
                spot=index.FirstOrDefault(c=>c.CandleTime==MarketClock.ToUtc(date,new TimeOnly(9,15)))?.Open;
                source="NIFTY_FIRST_0915_OPEN";
            }
            if (!spot.HasValue || spot.Value<=0)
            {
                if (live) return null;
                throw new InvalidOperationException($"No first stored NIFTY spot or first 09:15 cash candle exists for {date}; a later candle cannot substitute.");
            }
            return await _repository.SaveMorningSelectionAsync(MorningSelection.Create(date,expiry,spot.Value,source),ct);
        }
        finally { gate.Release(); }
    }

    public async Task<List<SignalPairDefinition>> GetSignalPairsAsync(DateOnly date,bool live,CancellationToken ct)
    {
        DateOnly expiry=await ResolveExpiryAsync(date,null,ct);
        return (await EnsureMorningSelectionAsync(date,expiry,live,ct))?.Pairs ?? [];
    }
    public async Task<List<SignalPairDefinition>> GetSignalPairsFromDatabaseAsync(DateOnly date,CancellationToken ct) =>
        (await _repository.GetMorningSelectionAsync(date,ct))?.Pairs
        ?? throw new InvalidOperationException("Prepare the day's fixed morning contracts first.");
    public async Task<List<decimal>> GetSignalStrikesAsync(DateOnly date,bool live,CancellationToken ct) =>
        (await GetSignalPairsAsync(date,live,ct)).SelectMany(p=>new[]{p.CallStrike,p.PutStrike}).Distinct().ToList();

    public async Task PrepareBacktestDayAsync(DateOnly date,DateOnly expiry,int hedgeDistance,CancellationToken ct)
    {
        ValidateHedgeDistance(hedgeDistance);
        if (date>MarketClock.IndiaDateNow()) throw new ArgumentException("Cannot backtest a future date.");
        await EnsureMorningSelectionAsync(date,expiry,false,ct);
        await EnsureSignalContractsAsync(date,expiry,false,ct);
    }
    public async Task EnsureSignalContractsAsync(DateOnly date,DateOnly expiry,bool live,CancellationToken ct)
    {
        var selection=await EnsureMorningSelectionAsync(date,expiry,live,ct);
        if (selection==null) return;
        // Cash first for same-time delta; exactly four CE and four PE contracts.
        await EnsureIndexAsync(date,live,ct);
        foreach (var pair in selection.Pairs)
        {
            await EnsureContractAsync(date,expiry,pair.CallStrike,"CE",live,ct);
            await EnsureContractAsync(date,expiry,pair.PutStrike,"PE",live,ct);
        }
    }
    public async Task<bool> IsSelectedContractAsync(DateOnly date,decimal strike,string type,CancellationToken ct) =>
        (await _repository.GetMorningSelectionAsync(date,ct))?.Contains(strike,NormalizeOptionType(type)) == true;

    public async Task<List<OptionCandle>> EnsureContractAsync(DateOnly date,DateOnly expiry,decimal strike,
        string optionType,bool live,CancellationToken ct)
    {
        string type=NormalizeOptionType(optionType);
        var selection=await EnsureMorningSelectionAsync(date,expiry,live,ct);
        if (selection==null) return [];
        if (!selection.Contains(strike,type)) throw new ArgumentException(
            $"{strike} {type} is outside the eight contracts fixed from the first stored spot. No API download was made.");
        string key=$"{date}|{expiry}|{strike}|{type}";
        var gate=_locks.GetOrAdd(key,_=>new(1,1));
        await gate.WaitAsync(ct);
        try
        {
            var stored=await _repository.GetOptionCandlesAsync(date,expiry,strike,type,ct);
            if (stored.Any(c=>!c.Delta.HasValue))
            {
                await EnrichDeltasAsync(stored,ct);
                await _repository.UpsertCandlesAsync(stored,ct);
            }
            if ((!live && HasRequiredHistoricalWindow(stored)) || (live && !ShouldRefresh(key,stored)))
                return live ? CompletedOnly(stored) : stored;
            if (live) _lastLiveRequest[key]=DateTimeOffset.UtcNow;
            // Both modes request the SAME full-day range; never an incremental range.
            var fetched=await _breeze.GetOptionDayAsync(date,expiry,strike,type,live,ct);
            if (fetched.Count>0)
            {
                await EnrichDeltasAsync(fetched,ct);
                await _repository.UpsertCandlesAsync(fetched,ct);
            }
            // Trade only saved database rows, not the transient API result.
            stored=await _repository.GetOptionCandlesAsync(date,expiry,strike,type,ct);
            if (!live)
            {
                if (!HasRequiredHistoricalWindow(stored)) throw new InvalidOperationException(
                    $"Incomplete full-day data for {date} {strike} {type}. Returned rows were saved; retry downloads the whole day again. Backtest was not started.");
                await _repository.MarkHistoricalDayLoadedAsync(date,key,stored.Count,ct);
            }
            return live ? CompletedOnly(stored) : stored;
        }
        finally { gate.Release(); }
    }

    public async Task<List<OptionCandle>> GetStoredContractCandlesAsync(DateOnly date,DateOnly expiry,
        decimal strike,string type,bool live,CancellationToken ct)
    {
        var candles=await _repository.GetOptionCandlesAsync(date,expiry,strike,NormalizeOptionType(type),ct);
        return live ? CompletedOnly(candles) : candles;
    }
    public async Task<decimal?> TryGetLiveOptionPriceAsync(DateOnly date,DateOnly expiry,decimal strike,string type,CancellationToken ct)
    {
        if (!await IsSelectedContractAsync(date,strike,type,ct)) return null;
        var candles=await _repository.GetOptionCandlesAsync(date,expiry,strike,NormalizeOptionType(type),ct);
        var indiaNow=MarketClock.ToIndia(DateTimeOffset.UtcNow);
        var boundary=MarketClock.ToUtc(date,new TimeOnly(indiaNow.Hour,indiaNow.Minute-indiaNow.Minute%5));
        return candles.FirstOrDefault(c=>c.CandleTime==boundary)?.Open;
    }
    public async Task<HedgePreview> GetHedgePreviewAsync(DateOnly date,DateOnly expiry,decimal soldStrike,
        string soldOptionType,int distance,DateTimeOffset? replayTime,bool live,CancellationToken ct)
    {
        string type=NormalizeOptionType(soldOptionType);
        decimal strike=GetHedgeStrike(soldStrike,type,distance);
        var preview=new HedgePreview{OptionType=type,SoldStrike=soldStrike,HedgeDistance=distance,HedgeStrike=strike};
        if (!await IsSelectedContractAsync(date,strike,type,ct))
        {
            preview.Message="Hedge is outside today's fixed eight contracts. Position stays naked; naked stop still applies.";
            preview.PriceSource="OUTSIDE_MORNING_SET"; return preview;
        }
        // Preview and replay are database-only. Preparation / live worker owns downloads.
        var candles=await GetStoredContractCandlesAsync(date,expiry,strike,type,live,ct);
        DateTimeOffset? cutoff=live ? MarketClock.LatestCompletedFiveMinuteCandleStartUtc() : replayTime ?? MarketClock.ToUtc(date,new TimeOnly(15,15));
        var candle=cutoff.HasValue ? candles.LastOrDefault(c=>c.CandleTime<=cutoff.Value && c.IsComplete) : null;
        preview.IsAvailable=candle!=null; preview.Premium=candle?.Close; preview.PriceTime=candle?.CandleTime;
        preview.PriceSource=candle==null ? "UNAVAILABLE" : "DATABASE";
        preview.Message=candle==null ? "No completed candle is saved at this time." : "Stored completed candle";
        return preview;
    }
    public decimal GetHedgeStrike(decimal soldStrike,string type,int distance)
    { ValidateHedgeDistance(distance); return soldStrike+(NormalizeOptionType(type)=="CE" ? distance : -distance); }
    public void ValidateHedgeDistance(int distance)
    { if (distance<100 || distance%100!=0) throw new ArgumentException("Hedge distance must be a positive multiple of 100."); }
    public static decimal RoundToStrike(decimal value,int step) => Math.Round(value/step,MidpointRounding.ToEven)*step;

    private async Task EnrichDeltasAsync(List<OptionCandle> candles,CancellationToken ct)
    {
        if(candles.Count==0) return;
        await _repository.PopulateLegacyDeltasAsync(candles,ct);
        var index=(await _repository.GetIndexCandlesAsync(candles[0].SessionDate,ct)).ToDictionary(c=>c.CandleTime);
        foreach(var c in candles)
            if(!c.Delta.HasValue && index.TryGetValue(c.CandleTime,out var spot)) c.Delta=OptionDeltaCalculator.Estimate(c,spot.Close);
    }

    private async Task EnsureIndexAsync(DateOnly date,bool live,CancellationToken ct)
    {
        string key=$"{date}|NIFTY_CASH";
        var gate=_locks.GetOrAdd(key,_=>new(1,1));
        await gate.WaitAsync(ct);
        try
        {
            var stored=await _repository.GetIndexCandlesAsync(date,ct);
            if ((!live && HasRequiredHistoricalWindow(stored)) || (live && !ShouldRefresh(key,stored))) return;
            if (live) _lastLiveRequest[key]=DateTimeOffset.UtcNow;
            var fetched=await _breeze.GetIndexDayAsync(date,live,ct);
            await _repository.UpsertCandlesAsync(fetched,ct);
            stored=await _repository.GetIndexCandlesAsync(date,ct);
            if (!live && !HasRequiredHistoricalWindow(stored)) throw new InvalidOperationException(
                $"NIFTY {date}: incomplete full-day index data saved; no backtest was run.");
        }
        finally { gate.Release(); }
    }
    private bool ShouldRefresh(string key,List<OptionCandle> candles)
    {
        var expected=MarketClock.LatestCompletedFiveMinuteCandleStartUtc();
        var now = MarketClock.ToIndia(DateTimeOffset.UtcNow);
        bool duringSession = TimeOnly.FromDateTime(now.DateTime) < new TimeOnly(15,30);
        bool hasCurrentOpen = expected.HasValue && candles.Any(c=>c.CandleTime==expected.Value.AddMinutes(5) && c.Open>0);
        if (expected.HasValue && candles.Any(c=>c.IsComplete && c.CandleTime==expected.Value)
            && (!duringSession || hasCurrentOpen)) return false;
        return !_lastLiveRequest.TryGetValue(key,out var last) || DateTimeOffset.UtcNow-last>=TimeSpan.FromSeconds(30);
    }
    public static List<OptionCandle> CompletedOnly(IEnumerable<OptionCandle> candles)
    {
        var cutoff=MarketClock.LatestCompletedFiveMinuteCandleStartUtc();
        return cutoff.HasValue ? candles.Where(c=>c.IsComplete && c.CandleTime<=cutoff.Value).OrderBy(c=>c.CandleTime).ToList() : [];
    }
    public static bool HasRequiredHistoricalWindow(IReadOnlyCollection<OptionCandle> candles)
    {
        if (candles.Count==0) return false;
        DateOnly date=candles.First().SessionDate;
        var times=candles.Where(c=>c.SessionDate==date && c.IsComplete).Select(c=>c.CandleTime).ToHashSet();
        var start=MarketClock.ToUtc(date,new TimeOnly(9,15));
        // All 75 completed bars, 09:15 through 15:25, before calling a day complete.
        return Enumerable.Range(0,75).All(i=>times.Contains(start.AddMinutes(5*i)));
    }
    private static string NormalizeOptionType(string value) => value.ToUpperInvariant() switch
    { "CALL" or "CE"=>"CE", "PUT" or "PE"=>"PE", _=>throw new ArgumentException("Option type must be CE/Call or PE/Put.") };
}
