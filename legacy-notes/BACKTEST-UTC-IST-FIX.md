# Backtest UTC / IST and replay stability fix

- Breeze historical request/response timestamps are treated as UTC in the API.
- 03:45 UTC maps to 09:15 IST.
- Historical candles are filtered/displayed in IST from 09:15 through 15:30.
- PostgreSQL continues to store `TIMESTAMPTZ` in UTC.
- Historical load keys were versioned so days cached by the earlier bad timestamp parser are fetched again once.
- Old load markers are no longer trusted unless the stored candles actually cover the trading day.
- The strategy timeline is explicitly constrained to 09:15–15:30 IST.
- Live latest-completed-candle logic can advance through the 15:25 candle (completed at 15:30); the strategy EOD exit rule remains 15:15.
- Replay Next/Previous no longer toggles the global loading banner, which was shifting the page vertically on every click.
- The replay clock is explicitly formatted in Asia/Kolkata.
- Replay steps are generated from the fixed 09:15–15:30 IST market grid instead of intersecting every strike. One missing CE/PE bar can no longer truncate the entire day.
- Automatic hedge-preview refresh is suppressed when the reference contract has not changed, reducing unnecessary re-render/network activity while pressing Next.
