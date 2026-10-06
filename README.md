# NIFTY Premium Candle Trader — fixed morning contracts

This version replaces the previous strike-selection and candle-loading rules. Use this README; files in legacy-notes describe older versions.

## Morning contracts

Obtain NIFTY spot and expiry from the first stored daily row, ordered by tradetimeonly and expirydate, then freeze four CE and four PE contracts in PostgreSQL for the entire day. Restarting or later spot changes cannot change this selection. `R = Math.Round(spot / 100) * 100`, using the C# default midpoint-to-even behavior.

| Branch | Ordered CE strikes | Ordered PE strikes |
|---|---|---|
| R > spot | R−100, R, R+100, R+200 | R, R−100, R−200, R−300 |
| Otherwise (including equality) | R+300, R+200, R+100, R | R, R−100, R−200, R+100 |

Pair by array position: CE[0]/PE[0], CE[1]/PE[1], CE[2]/PE[2], CE[3]/PE[3]. Matching strikes is not required. The screen displays both strikes in each pair. For example, spot 22,470 gives CE 22,400 / PE 22,500 for pair 1.

First use `SELECT spotprise, expirydate FROM opstraoptiondataYYYY WHERE tradedateonly=@date ORDER BY tradetimeonly, expirydate LIMIT 1`, retaining the supplied expiry exclusion after 2025-09-02. There is no 09:20 time filter. If this source is unavailable, the fallback is the first regular-session NIFTY cash candle Open at 09:15; the saved source identifies this fallback. Never use a 09:20 Open or a later candle as a substitute. Expiry lookup follows the earliest-time/expiry ordering in supplied GetStrike1, including excluding 2025-09-25 for dates after 2025-09-02. Existing expiry-table lookup or a supplied expiry is the fallback because DBManager.GetStrike implementation was not supplied.

## Shared data flow

Both live and backtest request `5minute` candle data using the full session range `YYYY-MM-DDT03:45:00.000Z` to `YYYY-MM-DDT10:30:00.000Z`, as in your GetOptionPremium method. Expiry uses midnight UTC of its date. The necessary session/signature/historical request flow from the supplied wrapper is implemented in BreezeRestClient; the decompiled wrapper is retained in reference/ and is not compiled into the application.

Only the selected eight option contracts are downloaded automatically, plus NIFTY cash candles for the morning spot and delta estimates. No hedge contract outside this selection is downloaded. Existing chart/preview requests outside the set are rejected.

Backtest checks the database first. A complete cached session makes no historical API requests. A missing or incomplete contract causes a full-day request for that contract; rows are upserted before replay begins. Require all 75 completed five-minute bars, 09:15 through 15:25, in each contract and cash data. Incomplete API responses are saved, but replay is blocked with a clear error. Retry requests the entire day again. Stepping through a prepared replay reads only the database.

Live calls the same full-day range and saves the rows available so far. A forming bar is retained for paper execution at the current candle Open but cannot vote in a signal. Signals require the latest completed timestamp across all eight contracts. Missing snapshots are retried with a 30-second per-contract throttle. Completed rows cannot be overwritten by forming rows. No tick-local branch or derived money-flow/Greek arrays from the supplied method are required.

## Locked trading rules

The original 1% opposing candle-body rule stays enabled: `(Close−Open)/Open*100`, CE positive and PE negative for a bullish pair, reversed for bearish. At least two agreeing pairs, and more agreeing than opposing pairs, determine direction. Select the sold-side contract from this fixed set with absolute delta closest to 0.50. Use stored Opstra call/put delta where available; otherwise estimate delta from option premium and same-time NIFTY Close using implied volatility and Black–Scholes with zero interest. This fallback is an estimate and may differ from your unprovided CommonData.Calculation. A missing valid delta prevents entry.

Fresh direction, next-candle Open replay execution, two completed candles of cooldown after exit, 15:15 EOD exit, and configured delayed-hedge rules remain. Defaults: 7 lots × lot size 65 = 455 units, target ₹25,000, naked hedge trigger −₹5,000, naked stop −₹25,000, hedged stop −₹15,000, hedge distance 300. Live broker sizing continues to use broker instrument validation.

A 300-point hedge may lie outside the morning set. In that case the position remains naked and its naked stop applies; the preview/live message explains this. This implements your restriction to only those eight contracts. `MinimumAverageWeakerBodyPercent=0` keeps the experimental strength filter disabled. Set it only deliberately; no new profitability result is claimed by this update.

## Setup

Requirements: .NET SDK 10, Node.js compatible with Angular 18, PostgreSQL, Breeze API key/secret and current session token. Preserve your broker settings when replacing source files.

1. Set `ConnectionStrings:Trading` in api/appsettings.json or environment variables. Use your actual database/password.
2. Set Breeze API/session values. `UseSessionMaster=true` can read your existing session table; configure a token directly if required.
3. Set `Strategy:LegacySchema` to your legacy table schema (default trading; search-path/public fallback exists). Set `LegacyTimesAreUtc=true` when legacy candle/delta times are UTC, or false when they are IST. This affects delta alignment; the morning query always selects the earliest daily row.
4. The API applies api/Sql/schema.sql on startup, including additive columns and the frozen morning-selection table. Existing expiry/session/Opstra tables retain their roles. Back up your database before applying a source update.
5. From the package root, run `dotnet run --project api/NiftyPremiumTrader.Api.csproj`. In ui run `npm ci`, then `npm start`. Existing start-dev.ps1 is also supplied.
6. Broker execution remains disabled by default (`AllowBrokerOrders=false`). Use live paper mode to verify your actual data mapping before enabling broker orders.

Breeze offsetless candle timestamps are interpreted as IST; explicit UTC/offset timestamps are respected. PostgreSQL stores candle timestamps as UTC. Selection rule version MORNING_FIRST_STORED_100_V2 prevents reuse of the previous 09:20-based plan; old records are retained. Frozen selection can be inspected through the data controller morning-selection endpoint.

## Verification

Run `dotnet run --project verification/NiftyPremiumTrader.Verification.csproj -c Release` and `npm run build` from ui. See VALIDATION.md for results and limits. No user credentials are bundled.
