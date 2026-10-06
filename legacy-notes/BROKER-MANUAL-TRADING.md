# Kotak manual/system trade integration

## What changed

The live screen now includes a Kotak broker dashboard sourced directly from Kotak Neo:

- `SYSTEM`: order id is owned by `trading.strategy_trades` or carries the NPT strategy tag.
- `MANUAL_UI`: real order placed from the new Angular manual order form.
- `MANUAL_APP`: Kotak order/trade that is not known to this application, including trades placed from the Kotak mobile/web app.
- `MIXED`: the same open trading symbol has broker activity from more than one source, so ownership cannot safely be separated from the net position alone.

Kotak remains the source of truth for order book, trade book and open positions.

## Manual orders from Angular

The live page now supports:

- Expiry
- Strike
- CE / PE
- BUY / SELL
- Lots
- Lot size
- MARKET / LIMIT
- Limit price when LIMIT is selected

Manual UI orders are tagged `NPT:MANUAL:UI` and saved in `trading.manual_broker_orders` so later broker refreshes can classify them correctly.

## Position exit

Manual positions (`MANUAL_UI` and `MANUAL_APP`) can be exited from the UI with either MARKET or LIMIT.

`SYSTEM` and `MIXED` rows are intentionally not exited through the single-position manual exit button. A strategy position may contain a short leg plus an automatic hedge. Closing one broker row outside the strategy state machine can desynchronize local state and cause a duplicate order. System positions remain under the strategy's target / stop / EOD exit logic.

## Strategy overlap

The premium-candle strategy remains single-position:

- only one SYSTEM strategy trade can be open at once;
- new strategy signals are blocked while its current trade is open;
- after exit there is a two-candle cooldown before another fresh strategy entry;
- manually initiated Kotak positions are displayed separately and do not create a second SYSTEM strategy trade.

## Database upgrade

`schema.sql` creates `trading.manual_broker_orders` automatically at API startup through the existing schema initialization.

## Kotak polling

The Angular live screen refreshes Kotak broker activity about every 10 seconds. These are Kotak API requests and do not consume the configured ICICI 2,000-call market-data budget.
