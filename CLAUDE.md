# FaustusController

ExileApi (ExileCore) plugin that automates the Path of Exile currency exchange (Faustus).
Current state: rate capture, market discovery, scan automation, and route analysis/planning all work.
End goal: **full automation** — plan a route, execute every hop, verify, and audit results.

## Build

```powershell
$env:exapiPackage = "<path to your ExileApi-Compiled folder>"   # contains ExileCore.dll / GameOffsets.dll
dotnet build FaustusController.csproj
```

- `net10.0-windows`, x64, nullable + implicit usings enabled.
- Do not set output paths; the HUD compiles/loads plugins placed under `Plugins/Source` automatically.
- Deps: ImGui.NET, Newtonsoft.Json, SharpDX.Mathematics.

## Layout

| Folder | Contents |
|---|---|
| `Core/` | `FaustusController` plugin class (partial, split by concern: main/lifecycle, `.Picker`, `.Automation`, `.Discovery`, `.Routing`, `.Orders`, `.Render`) and `FaustusControllerSettings` |
| `Domain/` | Core value types: `ExchangeRates.cs` (pair keys, `RationalExchangeRate`, `ExchangeRateBook`, snapshots), `CurrencyCatalogue` |
| `Input/` | Human-like verified input: cursor tween, search focus/query, verified option move/click, picker button calibration, calibrated picker open, picker inspector |
| `Capture/` | Reading the exchange panel (`CurrencyExchangeRateCollector`) and JSON persistence (`RateCaptureJsonExporter`, capture models, legacy formats) |
| `Discovery/` | Active-market discovery, liquidity discovery, probe store, manual overrides, active refresh plan |
| `Scanning/` | Scan plan builder, single-pair scan, bounded scan |
| `Routing/` | Conversion graph, route analyzer (`CurrencyRouteAnalysis.cs`), route models, route execution plan export |

## Key concepts

- **Safety model**: every input capability is opt-in and defaults to off (`Allow*` toggles). Input only runs while PoE is the foreground window; automation cancels itself on any verification failure instead of guessing.
- **Verified input**: every mouse/keyboard action is target-verified against live game memory before and after acting (tween to target, re-read UI state, abort on mismatch).
- **Hotkeys & permissions**: the single source of truth is `Core/FaustusControllerSettings.cs`. Read it there; do not duplicate the bindings in this file.
- **Persistence**: all artifacts are JSON in the plugin `ConfigDirectory`, league-scoped file names, with `SchemaVersion` fields. Exports: rate captures, bounded-scan manifest, market discovery, conversion graph, discovery probes/overrides, active refresh plan, route request/analysis, route execution plan.
- **Rates** are exact rationals (`GetUnits:GiveUnits`), never floats. Route analysis is whole-unit exact math with liquidity limits, gold costs, and quote-age freshness constraints.

## Roadmap to full automation

Work these in order; each step builds on the previous one.

1. ~~**Live inventory from picker**~~ — DONE: `SyncInventoryFromPicker` (`Core/FaustusController.Orders.cs`) merges visible picker `Owned` counts into the route request's `InventoryBalances`.
2. ~~**Real gold costs from placed orders**~~ — DONE: `CalibrateGoldCostFromOrders` sets `GoldCostPerHop` to the median gold cost of placed orders.
3. **Order amount input** — type into `OfferedItemCountInput`/`WantedItemCountInput` (foreground-gated, verified, cancelable).
4. **Order placement state machine** — select pair, set amount, place order, verify result, abort on any failure.
5. **Single-hop execution** — execute one step from the route plan end-to-end with pre/post rate verification.
6. **Multi-hop route execution** — chain all plan steps sequentially with per-hop verification and cancel-on-failure.
7. **Post-execution audit** — compare actual received units against `ExpectedReceived`; log discrepancies.
8. **Route re-analysis on shortfall** — if actual output < expected, re-run route analysis with updated inventory to find a recovery route.
9. **Gold budget enforcement** — track cumulative gold across executed steps; halt at `GoldBudget`.
10. **Automation history log** — append each executed step to a league-scoped JSON audit trail.

**Required alongside steps 4–6**: competing trades (orders not fulfilled instantly) sit in a waiting period. Viewing listings is DONE: `ExportPlacedOrders` (`Core/FaustusController.Orders.cs`) reads `CurrencyExchangePanel.Orders` and exports pending/completed/canceled orders to `FaustusController_placed-orders-<League>.json`. Still needed with steps 4–6: wait-on and cancel actions for outstanding orders.

## Maintaining this codebase

- **Folder discipline**: new types go in the folder matching their concern (table above). One primary type per file; small DTO/model records may share a `*Models.cs` file next to their consumer.
- **`FaustusController` partials**: keep the main file to fields + `Initialise`/`AreaChange`/`Tick`. Put new methods in the partial matching their concern; create a new `FaustusController.<Concern>.cs` partial if none fits. Don't let any partial grow past ~600 lines.
- **Namespace**: everything stays in the single `FaustusController` file-scoped namespace — moving files never requires namespace edits.
- **New automation features** must follow the existing pattern: an `Allow*` toggle (default off), foreground gating, verified state machine controller (see `Scanning/SinglePairScanController.cs` as the template), cancel-with-reason on any mismatch, and a status string surfaced in `Render`.
- **Legacy scan controllers**: `SinglePairScanController` and `BoundedScanController` are kept as the state-machine reference and are still ticked/cancelled, but have no start entry points — their hotkeys, `Allow*` toggles, and `PairsPerBoundedScan` were removed when liquidity discovery + active refresh superseded them. Restore a hotkey + toggle to reactivate.
- **Persistence changes**: bump `SchemaVersion`, keep readers tolerant of old files (see `Capture/LegacyRateCaptureFormats.cs` for the migration pattern).
- **Verify**: `dotnet build` must pass with 0 warnings before committing.
- **This file**: keep it short. Update the Roadmap checklist as steps land (mark done / remove), record only durable architecture decisions here — not session-by-session implementation logs. Point to source files as the source of truth instead of duplicating their contents.
