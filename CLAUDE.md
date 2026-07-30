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
- **SDK read probe** (`DumpSdkReadsKey`): `Core/FaustusController.SdkProbe.cs` dumps every ExileCore read the plugin depends on (panel, pair, market rate, stock, count inputs, picker options, orders) to `FaustusController_sdk-probe.txt` in the config directory. Run it first after any ExileApi/game update — the plugin compiles cleanly even when offsets behind unchanged member names read garbage, and the probe finds the first bad read from facts instead of guesses.
- **Persistence**: all artifacts are JSON in the plugin `ConfigDirectory`, league-scoped file names, with `SchemaVersion` fields. Exports: rate captures, bounded-scan manifest, market discovery, conversion graph, discovery probes/overrides, active refresh plan, route request/analysis, route execution plan.
- **Rates** are exact rationals (`GetUnits:GiveUnits`), never floats. Route analysis is whole-unit exact math with liquidity limits, gold costs, and quote-age freshness constraints.
- **Route analysis modes** (`Routing/CurrencyRouteAnalysis.cs`): when the request's start ≠ target it is an acyclic Start→Target search ranked by target units. When start == target (needs ≥2 hops) it is **cycle mode** — profitable loops back to the start currency (e.g. `Chaos→Scarab→Divine→Chaos`), ranked by net start-currency gain. Net gain is exact: `terminal received + leftover start-currency from hop 1 − StartAmount`; stranded non-start currencies are surfaced separately, not counted. `RequireProfit` (default on for the shipped Chaos→Chaos request) drops non-positive loops, so `RouteFound=false` means no profitable loop exists. The single structural enabler is that the target-termination check runs before cycle rejection in `Search`; the graph already carries the needed `Chaos/Divine→X` (competing) and `X→Chaos/Divine` (immediate) edges.
- **Tradable categories** (`Discovery/TradableCategoryResolver.cs`): `tradables.json` (a curated, intentionally-partial map of category name → item-name array; `tradables.txt` legacy format still parses) is loaded at `Initialise` by probing several roots (plugin dir chain, `ConfigDirectory` chain, `AppContext.BaseDirectory`). It is **authoritative by name only — no metadata-path guessing** — so any currency not on the list resolves to `Other`. The per-category `Include*` toggles in settings drive `ApplyCategoryFilters` (`Core/FaustusController.Discovery.cs`), which folds every currency in a disabled category into the effective `ForceSkip` set (except Chaos/Divine pivots and manual `ForceInclude`). A category signature triggers an override rebuild when a toggle flips. Load result + per-category counts show in `Render`; a red line means the file was not found/parsed (everything would be `Other`).
- **Stable-rate sample count** (`StableRateSampleCount`, 1–5): `SinglePairScanController.StableRateSampleTarget` gates capture. There are four scan-controller instances (host standalone + inner ones in liquidity discovery, bounded scan, order staging); the host `Tick` pushes the setting to **all** of them each frame via forwarding properties, so discovery/staging honour it too.

## Roadmap to full automation

Work these in order; each step builds on the previous one.

1. ~~**Live inventory from picker**~~ — DONE: `SyncInventoryFromPicker` (`Core/FaustusController.Orders.cs`) merges visible picker `Owned` counts into the route request's `InventoryBalances`.
2. ~~**Real gold costs from placed orders**~~ — DONE: `CalibrateGoldCostFromOrders` sets `GoldCostPerHop` to the median gold cost of placed orders.
3. ~~**Order amount input**~~ — DONE: `CurrencyAmountInputController` (`Input/CurrencyAmountInputController.cs`) tweens to, clicks, and types a verified amount into `OfferedItemCountInput`/`WantedItemCountInput`; F3/F4 type the selected route's first-hop `Spent`/`Received` behind the `AllowOrderAmountInput` toggle. Amount hotkeys must be non-character keys (F-keys): NumPad digits leak a literal digit into whichever count input is still focused.
4. ~~**Order placement state machine**~~ — DONE: `Orders/OrderStagingController.cs` stages a dry run (F6, `AllowOrderStaging`): selects the first-hop pair via `SinglePairScanController`, blocks staging when the trade should fill immediately but listed stock at the staged ratio or better can't cover the wanted amount (`TryValidateImmediateStock`), types both amounts via `CurrencyAmountInputController`, re-verifies the pair, then presses Enter once (`LockingInAmounts`) so the inputs defocus and the game enables Place Order. Count-input `IsActive` flags can stay latched in memory after a real defocus, so they only gate the 2s wait; the decisive checks are digits retained, pair still selected, and a final live `TryCaptureCurrentPair` re-capture that re-validates the immediate fill right before `Staged`. Both the initial and final pair captures are handed to the host via `TakeStagedSnapshot` and persisted through `StoreAndExportAutomatedSnapshot`, updating listings exactly like an Insert-triggered active refresh. `Orders/OrderPlacementController.cs` (F8, `AllowOrderPlacement`) finishes the hop: F8 runs the same staging flow, then tweens to the Place Order button (calibrated by hovering it and pressing F9; stored as the optional `PlaceOrderButton` point in the picker-button calibration file, preserved across F12 recalibration), revalidates pair/digits/target/cursor, clicks once, and verifies a new `PlayerOrderId` for the staged pair appears in `CurrencyExchangePanel.Orders` within 3s — success auto-runs the placed-orders export; F8 while running aborts both controllers.
5. ~~**Single-hop execution**~~ — DONE: `Orders/SingleHopExecutionController.cs` (F10, `AllowSingleHopExecution`) executes the selected route's first hop end-to-end. It coordinates the existing staging + placement controllers (observing their state, as placement already observes staging), adds an explicit **pre-execution rate gate** (the live `StagedImmediateRate` must give ≥ the planned wanted-per-offered via exact cross-multiply, else it cancels without clicking), then runs placement and builds a **post-execution audit** (`OfferedSpent`, `ActualReceived` from the placed order's ratio parts, `ReceivedShortfall`, `FullyFilled`) persisted to `FaustusController_hop-execution-<League>.json` (`Core/FaustusController.Execution.cs`). Staging exposes `StagedImmediateRate`; placement exposes `Outcome` (`PlacedOrderOutcome`). Hop resolution is shared with F6 via `TryBuildSelectedFirstHopStep`.
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

## Github Policy
Don't auto commit
