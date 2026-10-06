namespace NiftyPremiumTrader.Api.Services;

public static class MarketClock
{
    private static readonly TimeZoneInfo IndiaTimeZone = FindIndiaTimeZone();

    public static DateTimeOffset ToUtc(DateOnly date, TimeOnly time)
    {
        DateTime local = date.ToDateTime(time, DateTimeKind.Unspecified);
        DateTime utc = TimeZoneInfo.ConvertTimeToUtc(local, IndiaTimeZone);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    public static DateTimeOffset ToUtc(DateTime localIndiaTime)
    {
        DateTime unspecified = DateTime.SpecifyKind(
            localIndiaTime,
            DateTimeKind.Unspecified);

        DateTime utc = TimeZoneInfo.ConvertTimeToUtc(
            unspecified,
            IndiaTimeZone);

        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    public static DateTimeOffset ToIndia(DateTimeOffset utcTime)
    {
        return TimeZoneInfo.ConvertTime(utcTime, IndiaTimeZone);
    }

    public static DateOnly IndiaDateNow()
    {
        DateTimeOffset indiaNow = ToIndia(DateTimeOffset.UtcNow);
        return DateOnly.FromDateTime(indiaNow.DateTime);
    }

    public static TimeOnly IndiaTimeNow()
    {
        DateTimeOffset indiaNow = ToIndia(DateTimeOffset.UtcNow);
        return TimeOnly.FromDateTime(indiaNow.DateTime);
    }

    public static DateTimeOffset? LatestCompletedFiveMinuteCandleStartUtc()
    {
        DateTimeOffset indiaNow = ToIndia(DateTimeOffset.UtcNow);
        DateOnly date = DateOnly.FromDateTime(indiaNow.DateTime);
        TimeOnly time = TimeOnly.FromDateTime(indiaNow.DateTime);

        if (time < new TimeOnly(9, 20))
        {
            return null;
        }

        int roundedMinute = indiaNow.Minute - indiaNow.Minute % 5;

        DateTime currentBoundaryLocal = new(
            indiaNow.Year,
            indiaNow.Month,
            indiaNow.Day,
            indiaNow.Hour,
            roundedMinute,
            0,
            DateTimeKind.Unspecified);

        DateTime latestCompletedStartLocal = currentBoundaryLocal.AddMinutes(-5);
        DateTimeOffset latest = ToUtc(latestCompletedStartLocal);
        DateTimeOffset lastStrategyCandle = ToUtc(date, new TimeOnly(15, 25));

        if (latest > lastStrategyCandle)
        {
            return lastStrategyCandle;
        }

        return latest;
    }

    public static int RemainingFiveMinuteRefreshes(TimeOnly endTime)
    {
        DateTimeOffset indiaNow = ToIndia(DateTimeOffset.UtcNow);
        DateOnly date = DateOnly.FromDateTime(indiaNow.DateTime);
        DateTimeOffset end = ToUtc(date, endTime);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (now >= end)
        {
            return 0;
        }

        double remainingMinutes = (end - now).TotalMinutes;
        return Math.Max(1, (int)Math.Ceiling(remainingMinutes / 5.0));
    }

    private static TimeZoneInfo FindIndiaTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        }
        catch
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        }
    }
}
