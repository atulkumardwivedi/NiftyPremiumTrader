# Live time / future-candle fix

## Symptom
At approximately 11:00 India time, Live Status showed `Last completed candle = 15:15` and a signal based on that future candle.

## Cause
Today's PostgreSQL data already contained later candles from an earlier full-day/broken live load. LiveTradingService loaded the full contract day and selected `MAX(common candle time)`, so future rows could be selected. Refresh detection also used the latest stored timestamp, so a stale 15:15 row could suppress the current ICICI refresh.

## Fix
- Live strategy reads are capped at `MarketClock.LatestCompletedFiveMinuteCandleStartUtc()`.
- Live chart and hedge preview data are capped at the same cutoff.
- ICICI refresh checks now require the exact expected completed candle instead of comparing against `MAX(candle_time)`.
- Historical/backtest mode remains unchanged and can still read the full day.

At 11:00 IST, when candles are labelled by their start time, the latest completed 5-minute candle is 10:55.
