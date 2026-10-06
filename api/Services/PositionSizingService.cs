using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NiftyPremiumTrader.Api.Options;
using NiftyPremiumTrader.Api.Models;
namespace NiftyPremiumTrader.Api.Services;

public sealed record RiskSetup(int Lots, int CooldownCandles, decimal ProfitTarget,
 decimal HedgeTriggerLoss, decimal NakedCatastrophicStop, decimal HedgedStop,
 string Profile, string Evidence);
public sealed record ContractMargin(decimal Strike, string OptionType, int Quantity, decimal RequiredMargin, bool BrokerAccepted);
public sealed class SizingPreview
{
 public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.UtcNow;
 public DateOnly ExpiryDate { get; set; }
 public decimal AvailableMargin { get; set; }
 public decimal UsableMargin { get; set; }
 public decimal RequiredMargin { get; set; }
 public decimal HedgeCashReserve { get; set; }
 public decimal TotalRequired => RequiredMargin + HedgeCashReserve;
 public decimal Shortfall => Math.Max(0,TotalRequired-UsableMargin);
 public int Lots { get; set; }
 public int LotSize { get; set; }
 public int Quantity => Lots*LotSize;
 public bool Affordable { get; set; }
 public string Mode { get; set; } = "Manual";
 public string Message { get; set; } = "";
 public RiskSetup? Setup { get; set; }
 public List<ContractMargin> Contracts { get; set; } = [];
}
public sealed class PositionSizingService
{
 private readonly KotakNeoClient _client;
 private readonly KotakAuthenticationService _auth;
 private readonly KotakInstrumentService _instruments;
 private readonly CandleDataService _data;
 private readonly KotakNeoOptions _broker;
 private readonly PositionSizingOptions _options;
 private readonly StrategyOptions _strategy;
 private readonly List<RiskSetup> _historical;
 public PositionSizingService(KotakNeoClient client,KotakAuthenticationService auth,
  KotakInstrumentService instruments,CandleDataService data,IOptions<KotakNeoOptions> broker,
  IOptions<PositionSizingOptions> options,IOptions<StrategyOptions> strategy,IWebHostEnvironment env)
 {
  _client=client;_auth=auth;_instruments=instruments;_data=data;_broker=broker.Value;_options=options.Value;_strategy=strategy.Value;
  var path=Path.Combine(env.ContentRootPath,"historical-risk-setups.json");
  _historical=JsonSerializer.Deserialize<List<RiskSetup>>(File.ReadAllText(path)) ?? [];
  if(_options.MaximumLots<1||_options.MaximumLots>15||_options.MarginReservePercent<0||_options.MarginReservePercent>=100||_options.HedgeCashReservePerLot<0)
   throw new InvalidOperationException("Invalid PositionSizing limits or reserves.");
 }
 public RiskSetup GetSetup(int lots,int lotSize,string? profile=null)
 {
  if(lots<1||lots>_options.MaximumLots||lotSize<1)throw new ArgumentException($"Lots must be 1–{_options.MaximumLots}; lot size must be positive.");
  string name=profile ?? _options.DefaultProfile;
  if(name=="HistoricalBest")
  {
   if(lotSize!=65)throw new ArgumentException("HistoricalBest was researched only at lot size 65; select ScaledWinner for other broker lot sizes.");
   return _historical.Single(x=>x.Lots==lots);
  }
  if(name=="ConfiguredBaseline")
  {
   decimal f=(decimal)(lots*lotSize)/(_strategy.Lots*_strategy.LotSize);
   return new(lots,_strategy.CooldownCandles,Round(_strategy.ProfitTarget*f),Round(_strategy.HedgeTriggerLoss*f),Round(_strategy.NakedCatastrophicStop*f),Round(_strategy.HedgedStop*f),name,"Scaled original configured strategy");
  }
  if(name!="ScaledWinner")throw new ArgumentException("Unknown setup profile.");
  decimal factor=(decimal)(lots*lotSize)/975m;
  return new(lots,2,Round(49000*factor),Round(-8500*factor),Round(-70000*factor),Round(-10000*factor),name,"Scaled 15-lot historical winner; not a separately optimized result at each size");
 }
 private static decimal Round(decimal x)=>Math.Round(x,2,MidpointRounding.AwayFromZero);
 public async Task<decimal> AvailableAsync(CancellationToken ct)
 {
  await _auth.EnsureAuthenticatedAsync(ct);
  using var doc=await _client.ReadLimitsAsync(_options.LimitsPath,ct);
  var root=Unwrap(doc.RootElement);Validate(root);
  return Read(root,"Net","net","availableMargin","avlMrgn");
 }
 public static JsonElement Unwrap(JsonElement root)
 {
  for(int i=0;i<4;i++)
  {
   if(root.ValueKind==JsonValueKind.Object && root.TryGetProperty("data",out var data))root=data;
   else break;
  }
  if(root.ValueKind!=JsonValueKind.Object)throw new InvalidOperationException("Broker margin response is not an object; sizing blocked.");
  return root;
 }
 public static decimal Read(JsonElement root,params string[] keys)
 {
  foreach(var key in keys)foreach(var field in root.EnumerateObject())
   if(field.Name.Equals(key,StringComparison.OrdinalIgnoreCase)&&decimal.TryParse(field.Value.ToString(),NumberStyles.Number,CultureInfo.InvariantCulture,out var value))return value;
  throw new InvalidOperationException("Broker response lacks a valid "+string.Join('/',keys)+"; sizing blocked.");
 }
 private static void Validate(JsonElement root)
 {
  if(root.TryGetProperty("stat",out var stat)&&!stat.ToString().Equals("Ok",StringComparison.OrdinalIgnoreCase))
   throw new InvalidOperationException("Broker margin check failed; sizing blocked.");
 }
 private async Task<ContractMargin> QuoteAsync(KotakOptionInstrument instrument,int quantity,CancellationToken ct)
 {
  using var doc=await _client.ReadRequiredMarginAsync(_options.MarginPath,instrument,quantity,_broker.Product,ct);
  var root=Unwrap(doc.RootElement);Validate(root);
  // ordMrgn is the proposed order's margin. reqdMrgn can mean only the shortfall.
  decimal required=Read(root,"ordMrgn","requiredMargin","orderMargin");
  if(required<=0)throw new InvalidOperationException("Broker returned zero/negative order margin for a naked short; sizing blocked.");
  bool accepted=!root.TryGetProperty("rmsVldtd",out var rms)||rms.ToString().Equals("OK",StringComparison.OrdinalIgnoreCase);
  return new(instrument.Strike,instrument.OptionType,quantity,required,accepted);
 }
 public async Task<SizingPreview> PreviewAsync(DateOnly? requestedExpiry,int lots,bool automatic,string? profile,CancellationToken ct,
  decimal? exactStrike=null,string? exactType=null)
 {
  if(!automatic)GetSetup(lots,65,profile);
  decimal available=await AvailableAsync(ct);
  DateOnly date=MarketClock.IndiaDateNow();var expiry=await _data.ResolveExpiryAsync(date,requestedExpiry,ct);
  var selection=await _data.EnsureMorningSelectionAsync(date,expiry,true,ct)
   ?? throw new InvalidOperationException("First morning spot is not ready; required margin cannot be calculated yet.");
  var contracts=selection.Pairs.SelectMany(p=>new[]{(p.CallStrike,"CE"),(p.PutStrike,"PE")}).ToList();
  if(exactStrike.HasValue)
  {
   if(!selection.Contains(exactStrike.Value,exactType!))throw new ArgumentException("Short contract is outside the frozen morning selection.");
   contracts=[(exactStrike.Value,exactType!)];
  }
  var instruments=new List<KotakOptionInstrument>();
  foreach(var (strike,type) in contracts)instruments.Add(await _instruments.ResolveAsync(expiry,strike,type,ct));
  int lotSize=instruments[0].LotSize;
  if(lotSize<=0||instruments.Any(x=>x.LotSize!=lotSize))throw new InvalidOperationException("Broker contract lot sizes are missing/inconsistent; sizing blocked.");
  decimal usable=Math.Max(0,available)*(1-_options.MarginReservePercent/100m);
  int chosen=automatic?_options.MaximumLots:lots;
  while(chosen>=1)
  {
   var setup=GetSetup(chosen,lotSize,profile);var quotes=new List<ContractMargin>();
   foreach(var instrument in instruments)quotes.Add(await QuoteAsync(instrument,checked(chosen*lotSize),ct));
   decimal required=quotes.Max(x=>x.RequiredMargin);decimal hedgeReserve=_options.HedgeCashReservePerLot*chosen;
   bool riskOk=_options.MaximumNakedStopBudget<=0||-setup.NakedCatastrophicStop<=_options.MaximumNakedStopBudget;
   bool ok=required+hedgeReserve<=usable&&quotes.All(x=>x.BrokerAccepted)&&riskOk;
   var result=new SizingPreview{ExpiryDate=expiry,AvailableMargin=available,UsableMargin=usable,RequiredMargin=required,HedgeCashReserve=hedgeReserve,Lots=chosen,LotSize=lotSize,Affordable=ok,Mode=automatic?"Auto":"Manual",Setup=setup,Contracts=quotes,
    Message=ok?"Margin checked. Setup applies to the whole position.":riskOk?"Selected lots exceed usable funds or broker validation; entry blocked.":"Configured naked-stop risk budget exceeded; entry blocked."};
   if(ok||!automatic)return result;
   chosen--;
  }
  return new SizingPreview{ExpiryDate=expiry,AvailableMargin=available,UsableMargin=usable,Lots=0,LotSize=lotSize,Mode="Auto",Message="No affordable lot count. Entry blocked."};
 }
}
