# Kotak realtime WebSocket update

## What changed

- Kotak Neo SFeed WebSocket is used for live option ticks for every open Kotak position and every working order.
- The API dynamically subscribes when a position/order appears and unsubscribes when it is no longer needed.
- Every tick updates current premium/LTP and unrealized open-position P&L.
- Tick updates are pushed from ASP.NET Core to Angular through SignalR (`/hubs/broker`).
- Angular does not poll ICICI for broker P&L.
- ICICI/Breeze remains the only source for the strategy's 5-minute candles.
- Kotak remains the execution broker and the source of truth for positions/orders/trades.

## Timing

- Position LTP / open P&L: every Kotak market tick.
- Working-order current premium: every Kotak market tick.
- Order status / new mobile-app positions: REST reconciliation every 2 seconds while a working order exists, otherwise every 10 seconds.
- Strategy signal/hedge/target/stop decisions: unchanged and still based on completed ICICI 5-minute candles so live and backtest use the same rules.

## Angular package

The UI now uses the official SignalR JavaScript client:

```bash
npm install
```

`package.json` contains `@microsoft/signalr`.

## UI compile fixes

The NG8107 warnings reported for `liveStatus.state?.` were removed. `state` is non-nullable once `liveStatus` exists, so the template now uses `liveStatus.state.`.

The TS18048 errors around `signal.selectedAtmStrike` / `signal.direction` were fixed by explicitly checking `signal` before using those properties.

## UI columns

Open positions show:

- source: SYSTEM / MANUAL UI / MANUAL APP / MIXED
- average price
- live LTP/current premium
- tick-by-tick P&L
- LIVE/REST marker
- last tick time
- MARKET/LIMIT exit for allowed manual positions

Orders show:

- broker order status
- filled quantity
- average fill price
- current option premium from WebSocket
- LIVE/REST marker
- last tick time

## Important strategy behavior

Realtime tick P&L is display/account monitoring data. It does not change the strategy backtest rules. Strategy entries, automatic hedge activation and strategy exits continue to evaluate completed ICICI 5-minute candles.
