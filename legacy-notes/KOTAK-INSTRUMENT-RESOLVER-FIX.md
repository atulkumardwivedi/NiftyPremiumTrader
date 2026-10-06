# Kotak valid-strike resolver fix

The previous resolver trusted `pExpiryDate` / `lExpiryDate` from Kotak's NSE F&O scrip master. Those fields can be inconsistent for index options. A valid 06-Oct-2026 NIFTY contract could therefore be parsed with the wrong expiry and later fail exact lookup.

The resolver now:

1. Prefers `pScripRefKey` for NIFTY contract identity. Example: `NIFTY06OCT2622600.00CE` -> 06-Oct-2026 / 22600 / CE.
2. Falls back to the current weekly trading-symbol format. Example: `NIFTY26O0622600CE` -> 06-Oct-2026 / 22600 / CE.
3. Uses `pExpiryDate` / `lExpiryDate` only as a fallback and fixes the year from the trading symbol when possible.
4. Supports Unix epoch `lExpiryDate` values.
5. Accepts NIFTY identity from `pSymbolName`, `pAssetCode`, `pScripRefKey`, or `pTrdSymbol`.
6. Reads lot size from `lLotSize`, then `iLotSize`, then `iBoardLotQty`.
7. Re-downloads the current day's NSE F&O scrip master once when an instrument lookup misses, so a stale cached CSV cannot block a valid contract.
8. Returns more useful diagnostics listing parsed expiries/nearby strikes when a contract genuinely cannot be found.

Tested against representative current weekly symbols:
- NIFTY26O0622600CE -> 06-Oct-2026 / 22600 CE
- NIFTY26O0622500PE -> 06-Oct-2026 / 22500 PE
- NIFTY26O0621850PE -> 06-Oct-2026 / 21850 PE
