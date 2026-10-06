using Microsoft.Extensions.Options;
using NiftyPremiumTrader.Api.Data;
using NiftyPremiumTrader.Api.Models;
using NiftyPremiumTrader.Api.Options;
using NiftyPremiumTrader.Api.Services;

int assertions=0;
void Check(bool condition,string message) { assertions++; if(!condition) throw new Exception(message); }
DateOnly date=new(2025,1,1), expiry=new(2025,1,2);
var up=MorningSelection.Create(date,expiry,22470,"test");
Check(up.Pairs.Select(p=>p.CallStrike).SequenceEqual(new decimal[]{22400,22500,22600,22700}),"Rounded-up CE ordering");
Check(up.Pairs.Select(p=>p.PutStrike).SequenceEqual(new decimal[]{22500,22400,22300,22200}),"Rounded-up PE ordering");
var down=MorningSelection.Create(date,expiry,22530,"test");
Check(down.Pairs.Select(p=>p.CallStrike).SequenceEqual(new decimal[]{22800,22700,22600,22500}),"Else CE descending ordering");
Check(down.Pairs.Select(p=>p.PutStrike).SequenceEqual(new decimal[]{22500,22400,22300,22600}),"Else PE ordering, including final +100");
Check(MorningSelection.Create(date,expiry,22500,"test").Pairs[0].CallStrike==22800,"Exact strike goes to else");
Check(MorningSelection.Create(date,expiry,22450,"test").RoundedStrike==22400,"Default Math.Round ties to even");
Check(MorningSelection.Create(date,expiry,22550,"test").RoundedStrike==22600,"Odd midpoint rounds up to even");
Check(MarketClock.ToUtc(date,new TimeOnly(9,20)).TimeOfDay==new TimeSpan(3,50,0),"09:20 IST -> 03:50 UTC");
Check(BreezeMarketDataClient.BuildToDate(date,true)=="2025-01-01T10:30:00.000Z","Live requests whole day");
Check(BreezeMarketDataClient.ParseBreezeTime("2025-01-01 09:25:00")==MarketClock.ToUtc(date,new TimeOnly(9,25)),"Offsetless response is IST");
Check(BreezeMarketDataClient.ParseBreezeTime("2025-01-01T03:55:00Z")==MarketClock.ToUtc(date,new TimeOnly(9,25)),"Explicit UTC response preserved");
var opts=Options.Create(new StrategyOptions());
Check(opts.Value.Lots==7 && opts.Value.LotSize==65,"Defaults are seven lots of 65");
var fallbackStore=new MemoryStore{Spot=0};
var fallbackSource=new FixtureSource{SecondIndexOpen=24010};
var fallbackData=new CandleDataService(fallbackStore,fallbackSource,opts,Options.Create(new BreezeOptions()));
var firstSelection=await fallbackData.EnsureMorningSelectionAsync(date,expiry,false,default);
Check(firstSelection!.MorningSpot==22470,"Fallback uses first 09:15 cash Open, not changed 09:20 Open");
Check(firstSelection.SpotSource=="NIFTY_FIRST_0915_OPEN","First-candle fallback source is explicit");
Check(fallbackSource.OptionCalls==0,"Morning selection fallback fetches only index");
var signalService=new PremiumSignalService(opts);
DateTimeOffset time=MarketClock.ToUtc(new DateOnly(2026,1,1),new TimeOnly(9,25));
decimal[] co=[136.2m,78.25m,38.7m,17.1m],cc=[151.75m,90m,45.5m,20.5m];
decimal[] po=[100.7m,57.7m,31.85m,17.1m],pc=[88.2m,50.6m,27.55m,15.05m];
var real=MorningSelection.Create(new DateOnly(2026,1,1),new DateOnly(2026,1,6),26170,"test");
var calls=new Dictionary<decimal,List<OptionCandle>>();var puts=new Dictionary<decimal,List<OptionCandle>>();
for(int i=0;i<4;i++)
{
    var p=real.Pairs[i];
    calls[p.CallStrike]=[new(){CandleTime=time,Strike=p.CallStrike,OptionType="CE",Open=co[i],Close=cc[i],Delta=.7m}];
    puts[p.PutStrike]=[new(){CandleTime=time,Strike=p.PutStrike,OptionType="PE",Open=po[i],Close=pc[i],Delta=i==0 ? -.51298m : -.25m}];
}
var pairs=PremiumSignalService.BuildPairs(real.Pairs,calls,puts,time);
var signal=signalService.Evaluate(time,pairs);
Check(signal.Direction==SignalDirection.Bullish && signal.BullishPairs==4,"Real different-strike candle pairs are bullish");
Check(signal.SelectedAtmStrike==26200,"Sell PE closest to absolute delta 0.50");
Check(signal.PairDetails[0].CallStrike==26100 && signal.PairDetails[0].PutStrike==26200,"UI exposes both strikes");
pairs[0].Call!.IsComplete=false;
Check(signalService.Evaluate(time,pairs).BullishPairs==3,"Forming bar cannot vote");

var store=new MemoryStore();var source=new FixtureSource();
var data=new CandleDataService(store,source,opts,Options.Create(new BreezeOptions()));
await data.PrepareBacktestDayAsync(date,expiry,300,default);
Check(source.OptionCalls==8 && source.IndexCalls==1,"First prepare downloads exactly eight options plus cash");
Check(store.Stored.Count==675,"All day rows saved to DB before replay");
store.Spot=24010;
Check((await data.EnsureMorningSelectionAsync(date,expiry,false,default))!.MorningSpot==22470,"Morning plan cannot change after later spot/restart");
await data.PrepareBacktestDayAsync(date,expiry,300,default);
Check(source.OptionCalls==8 && source.IndexCalls==1,"Cached full day makes zero further API calls");
var engine=new StrategyEngine(store,data,signalService,opts);
var timeline=await engine.GetTimelineAsync(date,expiry,default);
var step=await engine.RunDayAsync(date,expiry,300,6,75,3,default);
Check(timeline.Count==76 && source.OptionCalls==8,"Replay is database-only");
var day=await engine.RunDayAsync(date,expiry,300,6,75,null,default);
Check(day.Trades.Count==1 && day.Trades[0].SignalTime==MarketClock.ToUtc(date,new TimeOnly(9,25)),"Fresh signal only at 09:25");
Check(day.Trades[0].EntryTime==MarketClock.ToUtc(date,new TimeOnly(9,30)),"Entry is next candle Open");
Check(day.Trades[0].ExitTime==MarketClock.ToUtc(date,new TimeOnly(15,15)),"EOD is exact 15:15 Open");
Check(source.OptionCalls==8,"Full replay and hedge decisions never download");
try { await data.EnsureContractAsync(date,expiry,23000,"CE",false,default); throw new Exception("Outside-set download allowed"); }
catch(ArgumentException) { Check(source.OptionCalls==8,"Outside set rejected before API call"); }
store.Stored.RemoveAt(store.Stored.FindIndex(c=>c.OptionType=="CE" && c.Strike==22400 && c.CandleTime==MarketClock.ToUtc(date,new TimeOnly(10,0))));
await data.PrepareBacktestDayAsync(date,expiry,300,default);
Check(source.OptionCalls==9,"One missing DB bar causes one full-contract day fetch");
var partialStore=new MemoryStore();var partialSource=new FixtureSource{Truncate=true};
var partialData=new CandleDataService(partialStore,partialSource,opts,Options.Create(new BreezeOptions()));
try { await partialData.PrepareBacktestDayAsync(date,expiry,300,default); throw new Exception("Incomplete data accepted"); }
catch(InvalidOperationException) { Check(partialStore.Stored.Count>0,"Partial API response saved before reporting incomplete day"); }
var bars=FixtureSource.Bars(date,null,null,null);
bars[0].IsComplete=false;
Check(!CandleDataService.HasRequiredHistoricalWindow(bars),"Forming bar cannot complete a historical session");
Console.WriteLine($"PASS: {assertions} assertions (ordered branches, actual signal, timestamps, cache, full-day fetch, replay, incomplete data).");

sealed class FixtureSource : IBreezeDaySource
{
    public int OptionCalls,IndexCalls; public bool Truncate; public decimal? SecondIndexOpen;
    public Task<List<OptionCandle>> GetIndexDayAsync(DateOnly d,bool live,CancellationToken ct)
    { IndexCalls++;var bars=Bars(d,null,null,null);if(SecondIndexOpen.HasValue){bars[1].Open=SecondIndexOpen.Value;bars[1].Close=SecondIndexOpen.Value;}return Task.FromResult(bars); }
    public Task<List<OptionCandle>> GetOptionDayAsync(DateOnly d,DateOnly e,decimal s,string t,bool live,CancellationToken ct)
    { OptionCalls++;var b=Bars(d,e,s,t);if(Truncate)b.RemoveAt(b.Count-1);return Task.FromResult(b); }
    public static List<OptionCandle> Bars(DateOnly d,DateOnly? e,decimal? s,string? t) => Enumerable.Range(0,75).Select(i=>new OptionCandle
    {
        SessionDate=d,CandleTime=MarketClock.ToUtc(d,new TimeOnly(9,15)).AddMinutes(i*5),ExpiryDate=e,Strike=s,OptionType=t,
        ProductType=t==null?"cash":"options",Open=t==null?22470:100,High=t==null?22470:102,Low=t==null?22470:98,
        Close=t==null?22470:i==2?(t=="CE"?102:98):100,Delta=t=="CE"?.5m:t=="PE"?-.5m:null,IsComplete=true
    }).ToList();
}
sealed class MemoryStore : ICandleStore
{
    public List<OptionCandle> Stored=[];public MorningSelection? Selection;public decimal Spot=22470;
    public void ConfigureLegacySource(string s,bool utc) { }
    public Task<MorningSelection?> GetMorningSelectionAsync(DateOnly d,CancellationToken ct)=>Task.FromResult(Selection);
    public Task<MorningSelection> SaveMorningSelectionAsync(MorningSelection s,CancellationToken ct){Selection??=s;return Task.FromResult(Selection);}
    public Task<DateOnly?> ResolveOpstraExpiryAsync(DateOnly d,CancellationToken ct)=>Task.FromResult<DateOnly?>(d.AddDays(1));
    public Task<DateOnly?> ResolveExpiryAsync(DateOnly d,CancellationToken ct)=>Task.FromResult<DateOnly?>(d.AddDays(1));
    public Task AddExpiryAsync(DateOnly e,CancellationToken ct)=>Task.CompletedTask;
    public Task<decimal?> GetMorningSpotAsync(DateOnly d,CancellationToken ct)=>Task.FromResult<decimal?>(Spot);
    public Task PopulateLegacyDeltasAsync(List<OptionCandle> c,CancellationToken ct)=>Task.CompletedTask;
    public Task<List<OptionCandle>> GetIndexCandlesAsync(DateOnly d,CancellationToken ct)=>Task.FromResult(Stored.Where(c=>c.SessionDate==d && c.ProductType=="cash").ToList());
    public Task<List<OptionCandle>> GetOptionCandlesAsync(DateOnly d,DateOnly e,decimal s,string t,CancellationToken ct)=>Task.FromResult(Stored.Where(c=>c.SessionDate==d && c.ExpiryDate==e && c.Strike==s && c.OptionType==t).OrderBy(c=>c.CandleTime).ToList());
    public async Task<OptionCandle?> GetOptionCandleAsync(DateOnly d,DateOnly e,decimal s,string t,DateTimeOffset time,CancellationToken ct)=>(await GetOptionCandlesAsync(d,e,s,t,ct)).FirstOrDefault(c=>c.CandleTime==time);
    public Task UpsertCandlesAsync(IReadOnlyCollection<OptionCandle> candles,CancellationToken ct)
    {
        foreach(var c in candles) {Stored.RemoveAll(x=>x.SessionDate==c.SessionDate && x.Strike==c.Strike && x.OptionType==c.OptionType && x.CandleTime==c.CandleTime);Stored.Add(c);}
        return Task.CompletedTask;
    }
    public Task MarkHistoricalDayLoadedAsync(DateOnly d,string key,int n,CancellationToken ct)=>Task.CompletedTask;
}
