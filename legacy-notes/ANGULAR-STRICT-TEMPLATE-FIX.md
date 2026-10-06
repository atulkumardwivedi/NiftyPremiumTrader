# Angular strict-template compile fix

This build fixes the strict Angular template errors reported on 2026-10-01.

## What was changed

Optional arrays/objects are normalized in `AppComponent` through non-nullable getters before they are used by the template.

Examples:

- `step.signal?.pairDetails.length` -> `stepSignalPairDetails.length > 0`
- `brokerDashboard?.positions.length` -> `brokerPositions.length > 0`
- `brokerDashboard?.orders.length` -> `brokerOrders.length > 0`
- `brokerDashboard?.trades.length` -> `brokerTrades.length > 0`
- `step?.completedTrades.length` -> `completedDayTrades.length > 0`
- `liveStatus?.trades.length` -> `liveTrades.length > 0`
- `rangeResult?.messages.length` -> `rangeMessages.length > 0`
- `rangeResult?.days.length` -> `rangeDays.length > 0`
- `rangeResult?.trades.length` -> `rangeTrades.length > 0`
- chart signal expression -> `chartSignal`
- hedge-preview enablement -> `hasHedgeReference`

The matching `*ngFor` expressions now use the same guaranteed arrays instead of optional chains.

## Validation performed

- Searched the complete Angular `src/app` tree for optional-chain array-length expressions and optional `*ngFor` collections: none remain.
- Parsed every TypeScript source file with TypeScript: no syntax/parse errors.
- The artifact environment could not complete `npm install`, so the final Angular compiler run must still be performed locally with your installed dependencies.

## Rebuild locally

```powershell
cd ui
Remove-Item node_modules -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item package-lock.json -Force -ErrorAction SilentlyContinue
npm install
npx ng build
npx ng serve
```

The prebundling/optimization message from Angular is a warning and is not the cause of the template compilation failure.
