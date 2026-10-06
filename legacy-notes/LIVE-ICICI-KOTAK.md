# Live mode: ICICI market data + Kotak Neo execution

The live system deliberately uses two different providers.

## ICICI / Breeze

ICICI is the only source used by the strategy for market prices and 5-minute candles.

At each live refresh boundary the API:

1. Refreshes NIFTY cash candles from 09:15 IST through the current time.
2. Determines the four signal strikes around the opening ATM.
3. Refreshes CE and PE from 09:15 IST through current time for all four signal strikes.
4. UPSERTs all returned candles into `trading.premium_candles_5m`.
5. If a hedge is active, refreshes the hedge contract the same way.
6. Calculates signal, P&L, target/stop and hedge trigger from PostgreSQL.

Normal five-minute refresh cost is approximately 9 ICICI data calls:

- 1 NIFTY cash
- 4 CE
- 4 PE

An active hedge can add 1 call, so budget planning reserves 10 calls per refresh.

## Kotak Neo

Kotak is used only when real broker execution is enabled.

Kotak handles:

- SELL short-option entry
- BUY delayed hedge
- BUY short-option exit
- SELL hedge exit
- order history / confirmed average fill price
- scrip-master resolution of NIFTY expiry, strike and option type

The strategy does not use Kotak quotes to create candles or generate signals.

## Real-order safety switch

Real orders are disabled by default:

```json
"LiveTrading": {
  "Enabled": true,
  "AllowBrokerOrders": false,
  "WorkerPollSeconds": 2
}
```

Fill the `KotakNeo` configuration and set `AllowBrokerOrders` to `true` only after paper-live testing.

## Kotak configuration

```json
"KotakNeo": {
  "Environment": "prod",
  "ConsumerKey": "",
  "MobileNumber": "",
  "Ucc": "",
  "Mpin": "",
  "TotpSecret": "",
  "NeoFinKey": "neotradeapi",
  "Product": "MIS",
  "ExchangeSegment": "nse_fo",
  "AutoLogin": true
}
```

`TotpSecret` is the Base32 secret used to generate the current TOTP locally. Do not commit real credentials to source control.
