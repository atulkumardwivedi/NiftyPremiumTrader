using NiftyPremiumTrader.Api.Models;

namespace NiftyPremiumTrader.Api.Services;

// Historical OHLC does not include Greeks. Prefer the user's stored Opstra delta;
// otherwise derive an explicitly estimated delta from premium/spot at this candle.
public static class OptionDeltaCalculator
{
    public static decimal? Estimate(OptionCandle option, decimal spot)
    {
        if (spot <= 0 || !option.Strike.HasValue || option.Strike.Value <= 0 || !option.ExpiryDate.HasValue) return null;
        double s = (double)spot, k = (double)option.Strike.Value, price = (double)option.Close;
        double years = (MarketClock.ToUtc(option.ExpiryDate.Value, new TimeOnly(15,30))
            - option.CandleTime.AddMinutes(5)).TotalDays / 365.0;
        if (years <= 0) return null;
        bool call = option.OptionType == "CE";
        double intrinsic = Math.Max(0, call ? s-k : k-s);
        if (price <= intrinsic || price >= (call ? s : k)) return null;
        double lo = .00001, hi = 8;
        for (int i=0; i<90; i++)
        {
            double sigma=(lo+hi)/2, d1=(Math.Log(s/k)+sigma*sigma*years/2)/(sigma*Math.Sqrt(years)), d2=d1-sigma*Math.Sqrt(years);
            double model = call ? s*Normal(d1)-k*Normal(d2) : k*Normal(-d2)-s*Normal(-d1);
            if (model < price) lo=sigma; else hi=sigma;
        }
        double iv=(lo+hi)/2;
        double d=(Math.Log(s/k)+iv*iv*years/2)/(iv*Math.Sqrt(years));
        return (decimal)(Normal(d)-(call ? 0 : 1));
    }

    private static double Normal(double x)
    {
        double t=1/(1+.2316419*Math.Abs(x));
        double tail=Math.Exp(-x*x/2)/Math.Sqrt(2*Math.PI)*t*
            (.319381530+t*(-.356563782+t*(1.781477937+t*(-1.821255978+t*1.330274429))));
        return x>=0 ? 1-tail : tail;
    }
}
