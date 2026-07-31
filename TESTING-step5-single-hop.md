# Testing notes — Step 5: Verified single-hop execution (F10)

Covers the `AllowSingleHopExecution` / F10 feature in
`Orders/SingleHopExecutionController.cs`, `Orders/HopExecutionModels.cs`,
`Core/FaustusController.Execution.cs`, plus the exposures on
`OrderStagingController` (`StagedRate`, `StagedMode`, `OfferedAmount`/`WantedAmount`,
`UncommittedRemainder`) and `OrderPlacementController` (`Outcome`), and the maker/resting-limit
extension (`Routing/ExecutionMode.cs`, `Orders/OrderExecutionMath.cs`,
`AllowRestingOrders` / `AllowCompetingOrderExecution`).

F10 is F8 (order placement) **plus** an explicit pre-execution rate/slippage gate and a
post-execution audit. It executes **hop 0 of the currently-selected analysis route** (shown in the
HUD / cycled with PageUp/PageDown). F8 is unchanged and remains the raw primitive.

**Immediate vs resting**: an immediate hop takes a listing now (fills on click); a resting-limit
(maker) hop posts an order at the competing ratio and normally rests as Pending. The HUD hop header
tags each hop `[immediate]` or `[RESTING LIMIT]` (orange). Resting hops appear only when the route
request has `AllowRestingOrders: true`, and F6/F8/F10 will only stage/place one when
`AllowCompetingOrderExecution` is also on. F10's contract for a resting hop **ends at verified
placement** — it never waits, collects, or cancels.

## Prerequisites / setup

1. Build is green: `dotnet build` → 0 warnings, 0 errors (with `$env:exapiPackage` set to the
   compiled folder).
2. In the plugin settings, enable **all** of these (F10 needs every placement permission plus its own
   toggle — `AreSingleHopExecutionPermissionsEnabled`):
   - `AllowSearchQueryInput`, `AllowVerifiedTargetMouseMove`, `AllowVerifiedOptionClick`,
     `AllowCalibratedPickerOpen`, `AllowOrderAmountInput`, `AllowOrderStaging`, `AllowOrderPlacement`,
     and **`AllowSingleHopExecution`**.
3. PoE foreground, Currency Exchange panel open, no picker open.
4. Calibrate the Place Order button: hover it, press **F9** (stored in the picker-button calibration
   file; survives F12 recalibration).
5. Press **Home** to run route analysis so a selected route with at least one hop exists.
   - Tip: for a Chaos→Chaos profit loop, hop 0 is `Chaos → X`; the audit currency names follow the hop.

## 1. Happy path — immediate hop (rate holds, immediate fill)

- Press **F10**.
- Expected HUD status progression (gold line, y≈400): staging → `Rate verified (staged <w>:<o> clears
  planned <get>:<give>); placing the immediate order.` → `Completed at placement (immediate): <spent>
  <Offered> -> <actual> <Wanted> fully filled; order <id> Completed…  Audit ->
  FaustusController_hop-execution-<League>.json.`
- A new order appears on the panel (same as F8), and placed-orders re-exports as before.
- Open `config/FaustusController/FaustusController_hop-execution-<League>.json` and verify:
  - `SchemaVersion: 2`, `ExecutionMode: "Immediate"`, `BookSide` populated.
  - `RatePreCheckPassed: true`, `PlacementVerified: true`.
  - `PlannedGetUnitsPerLot`/`PlannedGiveUnitsPerLot` = the hop's rate; `ExecutedGetUnits`/`ExecutedGiveUnits`
    = the live staged rate; `UncommittedRemainder: 0` (immediate always commits the full spend).
  - `OfferedSpent` = `OriginalOffered − RemainingOffered`; `ActualReceived` ≈ `RecomputedReceived`;
    `ReceivedShortfall: 0`; `CompletedAtPlacement: true`; `OutstandingAtAudit: false`; `FullyFilled: true`.
  - `PlayerOrderId`, `OrderStatus` (`Completed (filled)`), `PlacedOfferedRatioPart`/`PlacedWantedRatioPart`,
    `GoldCost` populated.

## 2. Pre-execution rate/slippage gate (market moved against the plan)

The gate compares the **actual staged amounts** (not just the book head) to the hop's **planned** rate
less `MaxRateSlippagePercent`, via exact overflow-safe arithmetic
(`OrderExecutionMath.PassesSlippageFloor`): place only if
`stagedWanted*plannedGive*100 ≥ stagedOffered*plannedGet*(100−slip)` (equality passes).

- To exercise it, make the live market worse than the analyzed plan before pressing F10 — e.g. let the
  best listing drift/expire so the top rate is worse than analysis captured (an aged graph is the easy
  lever), keep `MaxRateSlippagePercent` at 0, then F10.
- Expected: F10 stages, then **aborts before clicking** with status
  `Single-hop execution aborted: staged <w>:<o> is below the planned <get>:<give> floor (slippage 0%);
  no order placed.`
- Verify: **no new order** appears; no audit file is written for this attempt (audits are written only
  on a verified placed order).
- Raise `MaxRateSlippagePercent` and confirm a small adverse drift now passes; equality is the boundary.

Note: an immediate hop *also* refuses when insufficient stock is listed at the staged ratio
(`TryValidateImmediateStock`) — an "order is no longer available" staging cancel surfaced as `aborted
during staging`. Both correctly prevent a bad trade.

## 3. Audit shortfall (partial immediate fill)

- If an immediate order does not fully fill (partials), the audit writes with
  `ActualReceived < RecomputedReceived`, `ReceivedShortfall > 0`, `FullyFilled: false`.
- `FullyFilled` is true **only** for a terminal `Completed` order satisfying the staged wanted amount; a
  Pending order is `OutstandingAtAudit: true` regardless of any partial fill.

## 4. Safety / abort paths (all should block or cancel with a clear reason)

- F10 with `AllowSingleHopExecution` **off** (other toggles on) → blocked: "enable Allow Single-Hop
  Execution plus every order-placement toggle first."
- F10 with panel closed or Place Order **not calibrated** → blocked with the calibrate-first message.
- F10 with **no analysis run** / selected route has no hops → blocked: "run route analysis (Home)
  first." / "the selected route has no hops."
- **F10 again while running** → aborts the in-flight run ("cancelled by hotkey"); staging/placement are
  cancelled too.
- **Toggle off mid-run**: flip `AllowSingleHopExecution` off while executing → cancels on the next tick
  ("a required permission toggle was disabled"). Flipping a placement toggle off is also caught (by the
  existing placement guard; the coordinator then sees placement Faulted and cancels).
- **Area change** while running → cancels ("cancelled after area change").
- **Lose PoE foreground** mid-run → the underlying staging/placement controllers cancel on lost
  foreground; the coordinator surfaces it as `aborted during staging/placement`.

## 5. Maker / resting-limit orders (opt-in)

Follows the plan's in-game matrix. Requires a route request with `AllowRestingOrders: true`, then
**Home** to re-analyze.

1. **Analysis stays immediate-only when off**: with `AllowRestingOrders: false`, press Home. Confirm
   every displayed hop is `[immediate]` and the Home status shows `immediate-only (<n> resting edges
   excluded)`. Route JSON hops all have `ExecutionMode: "Immediate"`.
2. **Resting hops surface when on**: set `AllowRestingOrders: true`, Home. Confirm the HUD tags some hops
   `[RESTING LIMIT]` (orange), the detail line shows `RestingLimit | CompetingBook | …`, and the JSON
   hop `ExecutionMode`/`BookSide`/rate match. Home status shows `resting orders ON`.
3. **Permission refusal**: select a route whose first hop is `[RESTING LIMIT]`. With
   `AllowCompetingOrderExecution: false` (staging/placement toggles on), press **F6**, **F8**, **F10**
   in turn — each must refuse **before** moving the cursor or typing, citing "enable Allow Competing
   Order Execution".
4. **F6 resting dry run**: enable `AllowCompetingOrderExecution`. Press **F6**. Confirm it selects the
   pair, reads the live competing ratio, types `offered = floor(plannedSpent/give)*give` and
   `wanted = lots*get` (status names the uncommitted remainder if any), locks in, and **does not** click
   Place Order.
5. **F10 resting placement**: with single-hop permissions + `AllowCompetingOrderExecution`, press **F10**.
   Confirm exactly **one** matching order appears. HUD status: `Placed and OUTSTANDING (resting-limit):
   … order <id> is Pending. Manage it manually.` Audit JSON: `ExecutionMode: "RestingLimit"`,
   `OutstandingAtAudit: true`, `CompletedAtPlacement: false`, `FullyFilled: false`, `PlacementVerified:
   true`, and `PlacedOfferedRatioPart`/`PlacedWantedRatioPart` cross-multiply-equal the staged ratio. If
   it instead filled instantly, `CompletedAtPlacement: true`/`OutstandingAtAudit: false`.
6. **F5 rejects maker routes**: press **F5** on any route containing a resting hop. It must reject before
   executing hop 1 ("… is a RESTING LIMIT order; F5 executes immediate-only routes").
7. **Favorable-drift only**: if the competing head moves against the typed resting order between lock-in
   and final recapture, staging aborts before placement ("the competing head moved against the typed
   resting order"); a favorable/unchanged head proceeds.
8. **Foreground/permission loss before the click** cancels with no click; after any uncertain post-click
   result, inspect and manage the order manually (a local abort cannot cancel a server-side order).

## 6. Regression checks (must be unchanged)

- **Immediate-only routes** with `AllowRestingOrders: false` and `AllowCompetingOrderExecution: false`
  behave exactly as before through F6/F8/F10/F5.
- **F8** (place order) behaves as before for immediate hops — the staging/placement changes are additive
  (`StagedRate`/`StagedMode`, strengthened `Outcome` match) and the F8 entry path (`StartOrderPlacement`)
  is untouched.
- **F6** (staging dry run) still stages an immediate hop without any plan/rate gate; it shares
  `TryBuildHopContext` with F10.
- Route analysis / inventory sync / gold calibration still block while an execution run is in flight
  (`IsAnyAutomationRunning` includes the coordinator).

## Files of interest when debugging

- Coordinator + gate/audit logic: `Orders/SingleHopExecutionController.cs`
- Pure exact arithmetic (lots, slippage, drift, ratio match): `Orders/OrderExecutionMath.cs`
- Mode/provenance types: `Routing/ExecutionMode.cs`
- Persisted audit shape (schema 2): `Orders/HopExecutionModels.cs` → `FaustusController_hop-execution-<League>.json`
- Host wiring (start/tick/write/permissions): `Core/FaustusController.Execution.cs`
- Exposures: `OrderStagingController.StagedRate`/`StagedMode`/`UncommittedRemainder`, `OrderPlacementController.Outcome`
- HUD line: gold status at y≈400 in `Core/FaustusController.Render.cs`

## Not covered by this step

Multi-hop chaining (step 6), the appended execution history trail (step 10), and the persisted
route-execution-plan file (`CurrencyRouteExecutionPlanExporter`, wired in step 6). The audit here is the
**latest** execution only (overwrite), not an append log.
