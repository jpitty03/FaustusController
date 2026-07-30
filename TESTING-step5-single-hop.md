# Testing notes — Step 5: Verified single-hop execution (F10)

Covers the `AllowSingleHopExecution` / F10 feature added in
`Orders/SingleHopExecutionController.cs`, `Orders/HopExecutionModels.cs`,
`Core/FaustusController.Execution.cs`, plus the additive exposures on
`OrderStagingController` (`StagedImmediateRate`) and `OrderPlacementController` (`Outcome`).

F10 is F8 (order placement) **plus** an explicit pre-execution rate gate and a post-execution
audit. It executes **hop 0 of the currently-selected analysis route** (the one shown in the HUD /
cycled with PageUp/PageDown). F8 is unchanged and remains the raw primitive.

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

## 1. Happy path (rate holds, immediate fill)

- Press **F10**.
- Expected HUD status progression (gold line, y≈400): staging → `Rate verified (get:give >= planned …);
  placing the order.` → `Executed <spent> <Offered> -> <actual> <Wanted> (planned <n>; fully filled);
  order <id> Completed…  Audit -> FaustusController_hop-execution-<League>.json.`
- A new order appears on the panel (same as F8), and placed-orders re-exports as before.
- Open `config/FaustusController/FaustusController_hop-execution-<League>.json` and verify:
  - `RatePreCheckPassed: true`
  - `PlannedGetUnitsPerLot`/`PlannedGiveUnitsPerLot` = the hop's rate; `ExecutedGetUnits`/`ExecutedGiveUnits`
    = the live staged rate (≥ planned).
  - `OfferedSpent` = `OriginalOffered − RemainingOffered`; `ActualReceived` ≈ `PlannedReceived`;
    `ReceivedShortfall: 0`; `FullyFilled: true`.
  - `PlayerOrderId`, `OrderStatus` (`Completed (filled)`), `GoldCost` populated.

## 2. Pre-execution rate gate (market moved against the plan)

The gate compares the **live staged rate** to the **planned** rate via exact integer cross-multiply:
place only if `live.GetUnits * plannedGive >= plannedGet * live.GiveUnits` (live gives at least as many
wanted per offered as planned).

- To exercise it, make the live market worse than the analyzed plan before pressing F10 — e.g. let the
  best listing drift/expire so the top immediate rate is worse than what analysis captured (an aged
  graph is the easy lever), then F10.
- Expected: F10 stages, then **aborts before clicking** with status
  `Single-hop execution aborted: live rate <g>:<v> is worse than planned <g>:<v>; no order placed.`
- Verify: **no new order** appears; no audit file is written for this attempt (audits are written only
  on a placed/Completed order).

Note: staging *also* refuses when insufficient stock is listed at the staged ratio
(`TryValidateImmediateStock`) — that shows as an "order is no longer available" staging cancel, which
the coordinator surfaces as `aborted during staging`. Both outcomes correctly prevent a bad trade;
the rate-gate message above is the plan-vs-live check specifically.

## 3. Audit shortfall (partial / resting order)

- If an order does not fully fill immediately (rests as Pending or partials), the audit still writes on
  Completed-state detection with:
  - `ActualReceived < PlannedReceived`, `ReceivedShortfall > 0`, `FullyFilled: false`,
    `OrderStatus: Pending` (or partial).
- Since staging only stages trades whose immediate fill is covered by listed stock, this is the
  off-nominal case to watch for; it should be rare but must be logged, not hidden.

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

## 5. Regression checks (must be unchanged)

- **F8** (place order) behaves exactly as before — the staging/placement changes are additive
  (`StagedImmediateRate`, `Outcome`) and the F8 entry path (`StartOrderPlacement`) is untouched.
- **F6** (staging dry run) still stages without any plan/rate gate; it now shares
  `TryBuildSelectedFirstHopStep` with F10 but its behavior and messages are the same.
- Route analysis / inventory sync / gold calibration still block while an execution run is in flight
  (`IsAnyAutomationRunning` now includes the coordinator).

## Files of interest when debugging

- Coordinator + gate/audit logic: `Orders/SingleHopExecutionController.cs`
- Persisted audit shape: `Orders/HopExecutionModels.cs` → `FaustusController_hop-execution-<League>.json`
- Host wiring (start/tick/write/permissions): `Core/FaustusController.Execution.cs`
- Exposures: `OrderStagingController.StagedImmediateRate`, `OrderPlacementController.Outcome`
- HUD line: gold status at y≈400 in `Core/FaustusController.Render.cs`

## Not covered by this step

Multi-hop chaining (step 6), the appended execution history trail (step 10), and the persisted
route-execution-plan file (`CurrencyRouteExecutionPlanExporter`, wired in step 6). The audit here is the
**latest** execution only (overwrite), not an append log.
