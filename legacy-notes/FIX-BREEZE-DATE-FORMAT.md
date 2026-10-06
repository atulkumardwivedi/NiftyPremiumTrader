# Breeze live historical "Date not in Proper Format" fix

Two changes were made:

1. Live `to_date` is now the latest completed 5-minute boundary and is always formatted with zero milliseconds, e.g. `2026-10-01T05:00:00.000Z`.
2. NIFTY cash historical-v2 requests no longer send `expiry_date`, `right`, or `strike_price`; those are optional for cash and are omitted.

Option historical requests continue to send expiry/right/strike as required.
