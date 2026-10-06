using Microsoft.Extensions.Options;
using NiftyPremiumTrader.Api.Models;
using NiftyPremiumTrader.Api.Options;

namespace NiftyPremiumTrader.Api.Services;

public sealed class PremiumSignalService
{
    private readonly StrategyOptions _options;
    public PremiumSignalService(IOptions<StrategyOptions> options) { _options=options.Value; }

    public PremiumSignal Evaluate(DateTimeOffset candleTime,IReadOnlyCollection<CandlePair> pairs)
    {
        decimal threshold=_options.PairMovePercent/100m;
        var details=new List<PairSignalDetail>();
        foreach(var p in pairs.OrderBy(p=>p.PairNumber))
        {
            if (p.Call==null || p.Put==null || !p.Call.IsComplete || !p.Put.IsComplete || p.Call.Open<=0 || p.Put.Open<=0) continue;
            decimal ce=(p.Call.Close-p.Call.Open)/p.Call.Open;
            decimal pe=(p.Put.Close-p.Put.Open)/p.Put.Open;
            string direction=ce>=threshold && pe<=-threshold ? "BULLISH" : ce<=-threshold && pe>=threshold ? "BEARISH" : "NONE";
            details.Add(new PairSignalDetail
            {
                PairNumber=p.PairNumber, Strike=p.CallStrike, CallStrike=p.CallStrike, PutStrike=p.PutStrike,
                CallOpen=p.Call.Open,CallClose=p.Call.Close,CallChangePercent=ce*100,
                PutOpen=p.Put.Open,PutClose=p.Put.Close,PutChangePercent=pe*100,PairSignal=direction
            });
        }
        int bulls=details.Count(p=>p.PairSignal=="BULLISH"), bears=details.Count(p=>p.PairSignal=="BEARISH");
        SignalDirection dir=bulls>=_options.MinimumAgreeingPairs && bulls>bears ? SignalDirection.Bullish :
            bears>=_options.MinimumAgreeingPairs && bears>bulls ? SignalDirection.Bearish : SignalDirection.None;
        string agreed=dir==SignalDirection.Bullish ? "BULLISH" : "BEARISH";
        var agree=details.Where(p=>p.PairSignal==agreed).ToList();
        decimal strength=dir==SignalDirection.None || agree.Count==0 ? 0 :
            agree.Average(p=>Math.Min(Math.Abs(p.CallChangePercent),Math.Abs(p.PutChangePercent)));
        // Pick only from the fixed morning contracts of the side being sold.
        var candidates=pairs.Select(p=>dir==SignalDirection.Bullish ? p.Put : p.Call)
            .Where(c=>c!=null && c.IsComplete && c.Delta.HasValue && Math.Abs(c.Delta.Value)>0 && Math.Abs(c.Delta.Value)<=1)
            .Select(c=>c!).OrderBy(c=>Math.Abs(Math.Abs(c.Delta!.Value)-.5m)).ToList();
        return new PremiumSignal
        {
            CandleTime=candleTime,Direction=dir,BullishPairs=bulls,BearishPairs=bears,
            SelectedAtmStrike=dir==SignalDirection.None ? 0 : candidates.FirstOrDefault()?.Strike ?? 0,
            StrikeSelectionSource="OPSTRA_OR_ESTIMATED_DELTA",AverageWeakerBodyPercent=strength,
            StrengthFilterPassed=_options.MinimumAverageWeakerBodyPercent<=0 || strength>=_options.MinimumAverageWeakerBodyPercent,
            BullishStrikes=details.Where(p=>p.PairSignal=="BULLISH").Select(p=>p.CallStrike).ToList(),
            BearishStrikes=details.Where(p=>p.PairSignal=="BEARISH").Select(p=>p.CallStrike).ToList(),PairDetails=details
        };
    }

    public static List<CandlePair> BuildPairs(IEnumerable<SignalPairDefinition> definitions,
        IReadOnlyDictionary<decimal,List<OptionCandle>> calls,IReadOnlyDictionary<decimal,List<OptionCandle>> puts,
        DateTimeOffset time) => definitions.OrderBy(p=>p.PairNumber).Select(p=>new CandlePair
        {
            PairNumber=p.PairNumber,Strike=p.CallStrike,CallStrike=p.CallStrike,PutStrike=p.PutStrike,
            Call=calls[p.CallStrike].FirstOrDefault(c=>c.CandleTime==time),
            Put=puts[p.PutStrike].FirstOrDefault(c=>c.CandleTime==time)
        }).ToList();
}
