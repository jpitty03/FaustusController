# Testing notes — Multi-hop route execution (F5)

Covers `AllowMultiHopExecution` / F5 (`Orders/MultiHopExecutionController.cs`,
`Core/FaustusController.MultiHop.cs`). This is the most autonomous feature — it runs the whole selected
route unattended, moving real currency across several orders. **Test with a short, cheap route first.**

## What it does

One F5 press chains the selected analysis route. Per hop `i`:
1. **Execute** — drives the F10 executor (stage → pre-rate gate → place → audit).
2. **Wait for fill** — if the hop wasn't immediately filled, polls `panel.Orders` for that order's
   `PlayerOrderId` to become Completed (20s timeout → halt).
3. **Collect** — drives the F11 collection (proceeds → inventory → stash).
4. Funds hop `i+1` (exchange offers inventory-then-stash automatically).

Halts the whole chain on any hop fault, an unfilled order past timeout, a shortfall, or a `GoldBudget`
breach. Writes `FaustusController_multihop-execution-<League>.json` (per-hop audits + totals) at the end
(success or halt). F5 again aborts.

## Prerequisites

- Build green (`dotnet build` → 0/0).
- Enable **Allow Multi-Hop Execution** AND every single-hop-execution permission (Allow Order Placement +
  all staging toggles + Allow Single-Hop Execution) AND **Allow Order Collection**. All default off.
- Exchange + **stash** + **inventory** open; Place Order calibrated (F9); the covered left ~2 inventory
  columns blocked (F11 requirement); a route analyzed and selected (Home, then PageUp/Down to pick).

## 1. Two-hop first (cheap currency)

- Pick/point the route request at a short route whose hops use cheap currency. Run analysis (Home), F5.
- Watch the orange HUD line (y≈440): "executing hop 1/2 …" → "collecting hop 1 …" → "executing hop 2/2
  …" → "Multi-hop done: 2/2 hops executed; gold X; final received Y …  Run -> …json".
- Open `FaustusController_multihop-execution-<League>.json`: two hop audits, each `FullyFilled`,
  actual≈expected; `CompletedHopCount` = `PlannedHopCount`; `Success: true`.
- Confirm in-game: hop 1's proceeds went to stash and hop 2 offered them.

## 2. Full Chaos cycle

- Select a Chaos→…→Chaos profit loop and F5. It should end holding **more Chaos** than it started.
- The run file's `IsCycle: true`, `NetGainUnits` ≈ the analysis's net gain, `FinalReceived` ≈ returned
  Chaos.

## 3. Abort / halt paths (each should stop cleanly and write the run file)

- **F5 mid-run** → aborts; sub-controllers (executor/staging/placement/collection) torn down.
- **Toggle off mid-run** (any required permission) → cancels next tick.
- **Lose PoE foreground / area change** → cancels.
- **A hop that won't fill** (stage something that rests) → 20s timeout, halt at that hop, no further hops
  run.
- **Gold budget** — set `GoldBudget` in the route request low enough that the cumulative gold trips it;
  the chain halts before the next hop with a budget message.
- **Broken connectivity** — if the selected route's hop outputs don't chain
  (`Hops[i].Wanted != Hops[i+1].Offered`), F5 is blocked up front with a clear message (shouldn't happen
  for a real route, but it's guarded).

## 4. What to watch closely

- **Per-hop collection depends on F11 working** in your window layout — if collection reports
  covered/unreachable currency, the chain still proceeds (inventory-first offering covers it), but keep
  the left columns blocked so proceeds land visible.
- **Shortfall** — if a hop fills for less than planned, the run halts ("shortfall; recovery is step 8").
  That's expected; automatic recovery is a later step.
- If hop 2 fails to place because it can't fund the offer, check that hop 1's proceeds actually reached
  stash/inventory (the collection step) — that's the most likely real-world snag.

## Regression

- F10 (single-hop) and F11 (collection) still work standalone — the host ticks them only when a
  multi-hop chain isn't running; multi-hop drives them during a run. Route analysis / inventory sync /
  gold calibration are blocked while a chain runs (`IsAnyAutomationRunning` includes it).

## Live-rate recalculation + profitability floor (applies to F10 and F5)

Execution no longer types the stale analysis amounts. At staging time it **recomputes** the
offered/wanted to the live top market rate (whole lots, capped by that level's listed count), then the
executor gates placement on a profitability floor.

- **Better market** → the placed order asks for **more** than planned; the hop audit shows
  `RecomputedReceived > PlannedReceived` (and `ExecutedGetUnits:ExecutedGiveUnits` = the live rate).
- **Floor (F10 and every F5 hop)** = that hop's own analysis-planned rate (`Received/Spent`) less
  `MaxRateSlippagePercent`. A live rate at least as good as planned trades (recomputed, possibly up); a
  worse one aborts "below the floor" — unless it's within the slippage tolerance. (The analyzer's routes
  don't telescope — a hop can spend inventory — so there is no whole-loop rate-product floor.)
- To tolerate small adverse drift, raise `MaxRateSlippagePercent` (e.g. 2 = trade if the live rate is
  within 2% of planned); 0 (default) trades only when live ≥ planned.
- The multi-hop run JSON's per-hop entries now carry planned vs recomputed vs actual for auditing drift.

## Not in scope

Re-analysis on shortfall (step 8 — halts instead), full gold-budget planning (step 9 — this is a basic
cumulative halt), the appended history log (step 10 — this overwrites the latest run), and resting
competing orders beyond the 20s wait.
