# Kotak open-position price correction

This build fixes the broker-position mismatch seen on 01-Oct-2026.

## What was correct
Only positions with non-zero net quantity are shown. Kotak's Portfolio page may list older zero-quantity contracts, but they are not open positions.

## What was wrong
The previous custom binary SFeed decoder was based on the pre-migration market-feed packet layout. Kotak changed the market-feed protocol in September 2026, causing invalid LTP values and decimal overflow.

## Safe realtime path in this build
- Positions/order snapshot: Kotak REST.
- Open-position LTP: Kotak quote endpoint, batched once per second for all tracked open positions/working orders.
- Angular updates: SignalR.
- ICICI is not used for broker-position LTP/P&L.

## Position calculations
Average price follows Kotak's documented fields:
- Total Buy Qty = cfBuyQty + flBuyQty
- Total Sell Qty = cfSellQty + flSellQty
- Total Buy Amt = cfBuyAmt + buyAmt
- Total Sell Amt = cfSellAmt + sellAmt
- Average price is calculated from the dominant open side if avgPrc is absent/zero.

Live P&L follows:

    (Total Sell Amt - Total Buy Amt)
    + NetQty * CurrentLtp * multiplier * genNum/genDen * prcNum/prcDen

This preserves realized activity in the same contract instead of using only `(LTP - Avg) * NetQty`.

## Validation guards
- Quotes <= 0 are ignored.
- Implausible prices > 1,000,000 are ignored rather than displayed.
- The legacy binary WebSocket decoder is no longer used by the broker realtime service.
- Only up to 50 open/working instruments are requested in one quote batch.
