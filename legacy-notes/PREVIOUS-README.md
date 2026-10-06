# NIFTY Premium Candle Trader

A clean Angular + ASP.NET Core solution for the premium-candle strategy researched on the supplied 2025/2026 data.

The same strategy rules and trade state machine are used by:

- single-day step-by-step backtest;
- multi-day backtest;
- live paper trading;
- optional live Kotak Neo order execution.

This avoids maintaining one version of the logic for backtest and another version for live.

---

## 1. Solution structure

```text
NiftyPremiumTrader/
  api/                         ASP.NET Core .NET 10 API
    Controllers/
    Data/
    Models/
    Options/
    Services/
    Sql/schema.sql
    appsettings.json
  ui/                          Angular 18 standalone application
    src/app/
      components/premium-chart/
      models/
      services/
  README.md
  NiftyPremiumTrader.sln
```

---

## 2. Strategy implemented

The strategy is intentionally premium-only. Vega, bottom bar, OI, IV and futures charts are not required for entry.

The signal checks four same-strike CE/PE pairs. By default the strikes are based on the opening NIFTY ATM:

```text
ATM + 50
ATM
ATM - 50
ATM - 100
```

The offsets and strike step are configurable in `api/appsettings.json`.

### Bullish pair

```text
CE Close >= CE Open × 1.01
AND
PE Close <= PE Open × 0.99
```

### Bearish pair

```text
CE Close <= CE Open × 0.99
AND
PE Close >= PE Open × 1.01
```

A direction becomes a valid signal only when:

```text
at least 2 pairs agree
AND
agreeing pair count > opposite pair count
```

Trade side:

```text
Bullish signal -> SELL PE
Bearish signal -> SELL CE
```

The short strike is selected from the four signal pairs using CE/PE premium parity as a premium-only ATM proxy. This keeps the live implementation independent of Greek calculations.

### Timing rule - important

A completed 5-minute candle can create a signal, but it cannot fill a trade on the same candle.

```text
signal from candle i Close
        ↓
create pending action
        ↓
execute at candle i+1 Open
```

The same next-open rule is used for:

- entry;
- hedge entry;
- target exit;
- naked stop exit;
- hedged stop exit.

End-of-day exit is performed at the 15:15 candle Open.

### Default trade management

```text
Lots                      6
Lot size                 65
Quantity                450
Entry window          09:25 - 14:00
Profit target          +25,000 gross
Hedge trigger           -5,000 gross
Naked catastrophic stop -25,000 gross
Hedged stop             -15,000 combined gross
Default hedge distance      300 points
Cooldown                     2 completed candles
EOD exit                  15:15 Open
```

After a trade is closed, a new trade can occur later in the same day when a fresh premium state appears.

---

## 3. Database design

The API automatically creates the tables from `api/Sql/schema.sql` on startup.

### `trading.premium_candles_5m`

One row per contract / 5-minute candle.

Important columns:

```text
session_date
candle_time
stock_code
exchange_code
product_type
expiry_date
strike
option_type
open_price
high_price
low_price
close_price
volume
open_interest
data_source
```

Both historical and live candles use this table. The backtest therefore replays exactly the same data shape used by live mode.

The unique key prevents duplicate candles:

```text
(session_date, contract_key, candle_time)
```

### `trading.option_expiries`

Stores known option expiry dates.

The API also understands your existing `niftyexpiry` table. For historical dates it prefers:

1. expiry already present in candle data;
2. your existing `niftyexpiry` table;
3. the new `option_expiries` fallback table.

### `trading.api_daily_budget`

Stores the number of Breeze REST calls consumed today.

### `trading.strategy_trades`

Persists live/paper trade state, including:

- signal and entry;
- selected short strike;
- hedge distance / strike / premium;
- entry and exit order ids;
- gross and net P&L;
- best/worst marked P&L;
- exit reason.

Backtest results are returned to the UI rather than permanently stored.

---

## 4. DB-first / ICICI fallback data mechanism

All historical and chart requests use this sequence:

```text
Request date + expiry + strike + CE/PE
              ↓
Check premium_candles_5m
              ↓
Complete data exists?
      YES -> return DB data
      NO  -> call Breeze getHistoricalData(...)
              ↓
          UPSERT returned candles
              ↓
          return DB data
```

The endpoint can be used directly for any strike:

```http
POST /api/data/ensure-contract
    ?date=2026-09-25
    &expiryDate=2026-09-29
    &strike=23100
    &optionType=CE
    &liveMode=false
```

A normal day backtest automatically performs this check for all signal strikes, so you do not need to manually preload the database.

---

## 5. ICICI / Breeze integration

The solution uses the official C# package:

```xml
<PackageReference Include="com.breeze.breezeconnect" Version="1.0.7" />
```

The market-data adapter intentionally follows the same mechanism as your existing code:

```csharp
client.getHistoricalData(
    interval: "5minute",
    fromDate: fromDate,
    toDate: toDate,
    stockCode: "NIFTY",
    exchangeCode: "NFO",
    productType: "options",
    expiryDate: expiryDate,
    right: "call",
    strikePrice: strikePrice);
```

For puts only `right` changes to `put`.

The existing `sessionMaster.icicisession` row can be reused. Configure:

```json
"Breeze": {
  "ApiKey": "YOUR_API_KEY",
  "ApiSecret": "YOUR_API_SECRET",
  "SessionToken": "",
  "UseSessionMaster": true
}
```

If you do not want to use `sessionMaster`, set `UseSessionMaster` to `false` and populate `SessionToken`.

Do not commit real keys or session tokens to source control.

---

## 6. 2,000-call daily budget

Although the broker may publish a different platform limit, this application deliberately uses your stricter hard limit:

```json
"ApiBudget": {
  "DailyLimit": 2000,
  "ReservedForLive": 900,
  "LiveSafetyCalls": 150
}
```

Historical/backfill calls are stopped before they consume the calls reserved for live use.

### Normal live requirement

The signal uses four strikes and both CE and PE:

```text
4 strikes × 2 option types = 8 REST calls per refresh
```

A 5-minute refresh throughout a normal session is approximately 72-75 refreshes:

```text
8 × ~75 = ~600 signal-candle REST calls/day
```

Additional calls are used only for:

- first NIFTY opening-data load;
- hedge premium preview;
- entry/exit quote in paper mode;
- real order / order-status operations;
- missing-data recovery.

The background worker wakes every 2 seconds, but it does **not** call Breeze every 2 seconds. It calls Breeze only at the configured 5-minute boundary when a completed candle is missing from the DB.

If the remaining call budget is too low, `ApiBudgetService` automatically calculates a slower live refresh such as 10/15/20 minutes and the UI displays a warning.

---

## 7. Step-by-step backtest UI

Choose:

- trading date;
- expiry date (only required when the DB cannot resolve it);
- hedge distance.

Click **Start day replay**.

The UI displays:

- current 5-minute candle;
- Previous 5 min / Next 5 min buttons;
- all four CE/PE pair candle bodies;
- Bullish / Bearish / None per pair;
- current strategy signal;
- pending next-open action;
- automatically selected strike;
- active trade;
- hedge state;
- realized and unrealized P&L;
- trade log;
- premium candlestick chart.

A signal is visible on the signal candle. The actual entry appears only after the next 5-minute step, preserving causal execution.

---

## 8. Premium chart

The Angular UI contains a custom candlestick chart with no third-party chart dependency.

It displays:

- premium OHLC candles;
- signal marker;
- short entry marker;
- hedge activation marker;
- exit marker;
- replay clipping so future candles are hidden during step-by-step backtest.

The chart automatically follows the currently active short contract, and you can manually select another strike / CE / PE.

---

## 9. Hedge selection and preview

The default list includes:

```text
200
250
300
350
400
450
500
```

Any multiple of the configured strike step can be typed, for example 350 or 400.

For a PE short:

```text
hedge strike = short strike - hedge distance
```

For a CE short:

```text
hedge strike = short strike + hedge distance
```

The UI shows the calculated hedge strike and premium before/while replaying.

Historical preview reads the premium from the stored 5-minute candle at the current replay time. If the hedge contract is missing, one Breeze historical call loads and stores it first.

Live preview uses the Breeze quote endpoint and is included in the daily API budget.

---

## 10. Automatic hedge entry / exit

Backtest and live use the same rules:

```text
Trade gross P&L <= -5,000
        ↓
create pending HEDGE action
        ↓
BUY selected same-side OTM hedge at next candle Open
```

After the hedge is active, P&L is calculated from both legs:

```text
short P&L = (short entry - short current) × quantity
hedge P&L = (hedge current - hedge entry) × quantity
combined P&L = short P&L + hedge P&L
```

If combined P&L falls to the configured hedged stop, both legs are closed automatically.

---

## 11. Multi-day backtest

Choose From / To dates and click **Run range**.

For each trading day the API:

1. resolves weekly expiry;
2. checks the DB for the NIFTY opening candle;
3. determines four signal strikes;
4. checks all eight option contracts;
5. downloads only missing contract days;
6. replays the full day causally;
7. records every trade, hedge and exit in the result.

The UI reports:

- gross P&L;
- estimated costs;
- net P&L;
- total trades;
- win rate;
- average P&L/trade;
- hedge usage;
- average theoretical defined risk after hedge;
- max drawdown;
- profitable days;
- day-level results;
- every trade.

---

## 12. Live mode

Click **Start live**.

Default mode is **paper live**. ICICI/Breeze is the market-data provider and Kotak Neo is the execution broker when real orders are enabled.

At every configured live refresh boundary the API requests ICICI candles from **09:15 IST through the current time**, UPSERTs them into PostgreSQL, and then calculates the strategy from the database. The strategy never uses Kotak quotes to create signals.

The live workflow is:

```text
new completed 5-minute boundary
        ↓
ICICI: refresh NIFTY 09:15 -> now
        ↓
ICICI: refresh 4 CE + 4 PE signal contracts 09:15 -> now
        ↓
UPSERT all returned candles into PostgreSQL
        ↓
if hedge active: refresh that ICICI contract too
        ↓
run four-pair premium signal / P&L from DB
        ↓
paper mode -> use ICICI stored candle price
real mode  -> place entry/hedge/exit through Kotak Neo
        ↓
persist trade and Kotak order ids / confirmed fill prices
```

The live worker wakes every few seconds only to notice a new refresh boundary. It does not make ICICI calls between refresh boundaries. A normal 5-minute refresh is approximately 9 ICICI calls (1 NIFTY + 4 CE + 4 PE); an active hedge can make it 10.

---

## 13. Real Kotak Neo order mode

Real broker orders are deliberately protected by two switches:

1. API configuration:

```json
"LiveTrading": {
  "AllowBrokerOrders": false
}
```

2. Angular checkbox: **Send real Kotak Neo orders**.

Both must be enabled. ICICI remains market-data only.

The Kotak execution service:

1. logs in with the configured TOTP secret and MPIN;
2. resolves the exact NIFTY option token/trading symbol from the Kotak NSE F&O scrip master;
3. sends MIS market orders for short entry, delayed hedge, and both exit legs;
4. polls Kotak order history;
5. records the actual confirmed average fill price and broker order id.

Paper-live fills continue to use the latest completed ICICI candle stored in PostgreSQL.

Before enabling real orders, paper-test the complete live path and confirm the lot size shown by the UI matches the Kotak scrip master.

---

## 14. API endpoints

### Backtest

```text
POST /api/backtest/start
POST /api/backtest/step/{sessionId}?direction=1
POST /api/backtest/step/{sessionId}?direction=-1
POST /api/backtest/range
```

### Data

```text
GET  /api/data/candles
POST /api/data/ensure-contract
GET  /api/data/hedge-preview
GET  /api/data/budget
POST /api/data/expiry
```

### Live

```text
POST /api/live/start
POST /api/live/stop
GET  /api/live/status
```

---

## 15. Run the API

Requirements:

- .NET 10 SDK;
- PostgreSQL;
- valid Breeze API key / secret / daily session;
- existing `sessionMaster` optional.

Edit:

```text
api/appsettings.json
```

Then:

```powershell
cd api
dotnet restore
dotnet run
```

Default API URL:

```text
http://localhost:5080
```

The schema is created automatically at startup.

---

## 16. Run Angular

Requirements:

- Node 20+ / 22;
- Angular CLI 18 (or use the project-local CLI after npm install).

```powershell
cd ui
npm install
npm start
```

Default UI URL:

```text
http://localhost:4200
```

---

## 17. Important production checks

Before allowing real orders:

1. Verify one historical date against your existing `GetOptionPremium` output candle-by-candle.
2. Confirm Breeze `datetime` labels represent the same 5-minute interval assumed by the app.
3. Confirm weekly expiry resolution for the selected historical period.
4. Run paper live for several sessions.
5. Compare the UI entry price, hedge trigger, hedge premium and exit price with the broker terminal.
6. Verify the selected UI lot size matches the current Kotak scrip-master lot size.
7. Verify ICICI API/static-IP requirements applicable to your account for live market-data calls.
8. Verify Kotak TOTP/MPIN login and a small manual API order flow before automatic trading.
9. Keep `AllowBrokerOrders=false` until the complete paper path is verified.

The project deliberately preserves the next-candle execution rule used in the corrected research. Do not change backtest entry to the same signal candle Close/Open, because that would reintroduce look-ahead bias.

## ICICI REST market-data compatibility

The project does not use the old `com.breeze.breezeconnect` C# SDK `generateSession()` path, so it does not depend on the SDK's `getStockScriptList()` download.

ICICI/Breeze is used only for market data:

- Customer Details v1 to resolve the daily API session token.
- Historical Charts v2 for NIFTY cash and option 5-minute candles.
- Historical backfill and live morning-to-current refreshes are UPSERTed into PostgreSQL.

All real order placement and fill confirmation is handled by Kotak Neo.

## Historical replay loading and API budget

Historical replay now uses a one-time full-day cache per contract:

1. `Start day replay` ensures the full NIFTY cash day exists.
2. It resolves the opening ATM and the four configured signal strikes.
3. Each of the four CE and four PE contracts is fetched once for the complete day if missing.
4. A row is written to `trading.candle_day_loads` after a successful full-day load.
5. Previous / Next 5-minute replay reads PostgreSQL only. It does not re-download the 8 signal contracts.
6. A hedge strike is fetched only the first time that exact strike is previewed or required. The complete day is stored and reused afterwards.

A new historical day therefore normally needs about 9 historical data calls (1 NIFTY + 8 option contracts), plus the one customer/session-token resolution the first time the API process talks to Breeze. Existing loaded days use zero historical market-data calls for replay.

The API usage counter is intentionally not reset when the application restarts. Calls already made earlier in the day still appear in the counter.

## Position sizing

`Lots` and `Lot size` are now configurable in the Angular UI. They are sent to single-day backtest, multi-day backtest and live mode. Every trade records and displays:

- number of lots
- lot size
- total quantity

Total quantity is `Lots * Lot size`.

## Timestamp correction

Breeze historical v2 is requested with `03:45Z` to `10:30Z`. Returned candle timestamps are treated as UTC even when Breeze omits the trailing `Z`. They are converted to India time only for market-session filtering/display. This restores the full 09:15-15:15 replay instead of the earlier truncated 09:15-10:30 timeline.

## Kotak Neo TOTP

Kotak login uses `Otp.NET` and the Base32 seed in `KotakNeo:TotpSecret`. The seed is preserved; each login generates a fresh 6-digit code with `new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp()`.
