# Kotak order + instrument resolver fix

This build fixes two broker-side problems seen on 01-Oct-2026.

## 1. Valid NIFTY strikes were not resolving

Order resolution now prefers Kotak Neo's current Option Chain endpoint:

`GET /market-data/1.0/watchlist/option-chain?exchange=nse_fo&underlying=NIFTY&expiry=YYYY-MM-DD`

The response directly supplies:
- `neoSymbol` (`nse_fo|token`)
- trading `symbol`
- `optionType`
- `strikePrice`
- `common_data.mktLot`

The daily NSE F&O scrip master is retained only as a fallback.

This avoids relying on inconsistent expiry fields in some scrip-master rows.

## 2. Kotak stCode 1007 / "body invalid" on place order

The regular order payload was still sending a legacy `mp` (market protection)
field. Kotak Neo's current SDK migration guide removes that parameter from
`place_order()`.

The request now sends only the supported regular order fields:
`am,dq,es,pc,pr,pt,qt,rt,tp,ts,tt,ig,os`.

The API also validates exchange/product/order-type/side/validity/quantity before
sending a broker request.

## Dashboard resilience

Open positions are fetched independently from working orders. If the Kotak order
book temporarily returns a 400 response, existing open positions still remain
visible instead of the entire broker panel becoming empty.

## Lot size

NIFTY live/manual defaults remain 65. The actual option-chain `mktLot` is used
when resolving the contract.
