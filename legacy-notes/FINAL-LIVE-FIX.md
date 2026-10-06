# Final live-mode fixes — 01-Oct-2026

This build keeps one strategy engine and separates the two broker roles clearly:

- **ICICI Breeze**: NIFTY + option market data only.
- **Kotak Neo**: real entry, hedge and exit execution only.
- **PostgreSQL**: live candle cache, backtest data, and live trade state.

## Live candle flow

At each eligible 5-minute boundary the background worker requests ICICI history from the market open through the latest completed candle, then UPSERTs the returned data into `trading.premium_candles_5m`.

The Angular chart does **not** refresh ICICI. It reads the candles already stored in PostgreSQL. This prevents 5-second UI polling from consuming the ICICI daily API allowance.

Live strategy reads are capped at `MarketClock.LatestCompletedFiveMinuteCandleStartUtc()`. Rows later than the current completed candle are ignored even if an older test build inserted a full day into the table.

The live status displays both:

- **Market latest completed candle** — what should currently be available from the market.
- **Strategy processed candle** — the latest complete four-CE/four-PE candle set that the strategy actually processed.

## Hedge preview

`Refresh premium` no longer throws when the selected hedge strike has no completed 5-minute candle.

The lookup order is:

1. Current stored ICICI 5-minute candle.
2. Refresh that ICICI contract when required.
3. ICICI quote fallback.
4. Return an `IsAvailable=false` preview with a message instead of HTTP 500.

The UI always shows the calculated hedge strike when a short reference is available.

## One open trade only

The strategy allows multiple trades in one day, but they are **sequential**:

1. One short trade can be open.
2. A new premium signal while that trade is open cannot create another entry.
3. The open trade may receive one automatic hedge.
4. The trade exits by target / stop / EOD.
5. A two-completed-candle cooldown follows.
6. A fresh qualifying signal is then required for the next trade.

On live start, the API restores an open trade from `trading.strategy_trades`. Before any new entry it checks the database again. This prevents a normal application restart from creating a second strategy entry while the saved trade is still open.

## Live UI additions

The live panel now shows:

- API server India time
- market latest completed candle
- strategy processed candle
- signal candle time
- current signal and all four pair details
- entry status
- open short position
- current marked P&L
- hedge state
- cooldown remaining
- lots, lot size and quantity

The premium chart reloads when the expected completed candle advances, not only when a new signal is generated.

## Breeze date format

Option `expiry_date` is sent in the same full ISO format used by the existing ICICI integration:

`yyyy-MM-ddT00:00:00.000Z`

`from_date` and `to_date` also use the strict `.000Z` format.

## Kotak TOTP

Kotak authentication uses `Otp.NET`:

```csharp
byte[] secretBytes = Base32Encoding.ToBytes(_options.TotpSecret.Trim());
var totp = new Totp(secretBytes);
string currentCode = totp.ComputeTotp();
```

`TotpSecret` remains the permanent Base32 seed and is never replaced by the generated six-digit code.
