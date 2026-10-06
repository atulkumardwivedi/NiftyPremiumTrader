namespace NiftyPremiumTrader.Api.Models;

public sealed record SignalPairDefinition(int PairNumber, decimal CallStrike, decimal PutStrike);

public sealed class MorningSelection
{
    public const string RuleVersion = "MORNING_FIRST_STORED_100_V2";
    public DateOnly SessionDate { get; set; }
    public DateOnly ExpiryDate { get; set; }
    public decimal MorningSpot { get; set; }
    public decimal RoundedStrike { get; set; }
    public string SpotSource { get; set; } = "";
    public List<SignalPairDefinition> Pairs { get; set; } = [];

    public bool Contains(decimal strike, string optionType) => Pairs.Any(p =>
        (optionType == "CE" ? p.CallStrike : p.PutStrike) == strike);

    public static MorningSelection Create(DateOnly date, DateOnly expiry, decimal spot, string source)
    {
        if (spot <= 0) throw new ArgumentOutOfRangeException(nameof(spot));
        // Matches Math.Round in the supplied code, including ties-to-even.
        decimal rounded = Math.Round(spot / 100m, MidpointRounding.ToEven) * 100m;
        int[] ce = rounded > spot ? [-100, 0, 100, 200] : [300, 200, 100, 0];
        int[] pe = rounded > spot ? [0, -100, -200, -300] : [0, -100, -200, 100];
        return new MorningSelection
        {
            SessionDate = date, ExpiryDate = expiry, MorningSpot = spot,
            RoundedStrike = rounded, SpotSource = source,
            Pairs = Enumerable.Range(0, 4).Select(i =>
                new SignalPairDefinition(i + 1, rounded + ce[i], rounded + pe[i])).ToList()
        };
    }
}
