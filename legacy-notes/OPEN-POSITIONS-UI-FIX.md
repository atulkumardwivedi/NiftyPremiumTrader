# Open Positions / Live UI fix

This revision cleans up live trading around the issues reported on 01-Oct-2026.

## Fixed

- Kotak market-feed compile error: `byte` could not be passed to `Exchange(sbyte)`.
  `Exchange` now accepts `byte`, matching the binary packet type.
- Live broker screen now shows open NIFTY F&O positions only. Completed Kotak
  order/trade history is intentionally not rendered in live mode.
- Working orders are shown separately and collapsed by default.
- Open Positions is collapsible and every open position has:
  - realtime Kotak WebSocket LTP
  - realtime unrealized P&L
  - position source and role
  - Chart button
  - Exit MKT
  - Exit LMT with a per-row price field
- System strategy position has a dedicated short/hedge card. Before the hedge
  activates the planned hedge strike is displayed; after activation its entry,
  live LTP and live P&L are displayed.
- A manual exit of a SYSTEM/MIXED broker row immediately stops automatic live
  strategy processing to prevent the strategy from later submitting a duplicate
  exit. Broker state must be reconciled before restarting auto trading.
- Breeze Historical V2 timestamp parsing now accepts both:
  - explicit UTC/offset timestamps; and
  - offset-less timestamps such as `2026-10-01 09:15:00`, treated as India time.
- A transient empty live ICICI NIFTY response no longer crashes Start Live or
  real-order mode. The state remains running and retries the same boundary.
- Premium chart no longer waits for a directional signal. A neutral completed
  signal can select its ATM-ish contract so candle patterns are visible.
- In live mode, backtest history/range panels are hidden to keep the screen
  focused on live strategy status, open broker positions, hedge and chart.

## Data responsibilities

- ICICI Breeze: completed 5-minute NIFTY/option candles used by the strategy.
- Kotak Neo market WebSocket: realtime LTP/current premium and open-position P&L.
- SignalR: pushes Kotak ticks from ASP.NET Core to Angular.
- Kotak Neo REST: position/order reconciliation and actual order placement.

## Safety

The automatic strategy remains single-position: it will not open a second
SYSTEM strategy trade while a SYSTEM strategy trade is already open. Independent
manual Kotak positions may coexist and are visible in the open positions table.
