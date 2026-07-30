# Testing notes — Order collection (F11)

Covers `AllowOrderCollection` / F11 (`Orders/OrderCollectionController.cs`,
`Core/FaustusController.Collection.cs`). This is the highest-risk feature so far — it moves real
currency with ctrl-right-click and ctrl+shift+right-click. Test with **cheap/expendable currency
first**.

## What it does

One F11 run, two phases (verified per click; F11 again aborts):
- **Phase 1 (exchange → inventory):** for each **Completed** order, ctrl-right-click its bought-currency
  slot (order-element child `<4>`), and — when the order has leftover offered units
  (`OfferedItemStackSize > 0`) — also ctrl-right-click the leftover slot `<5>`. Verifies by the slot's
  icon clearing / the order leaving `panel.Orders`.
- **Phase 2 (inventory → stash):** for each currency collected in Phase 1, ctrl+shift+right-click one
  **visible** inventory stack (which sweeps all stacks of that type, visible + covered, to stash).
  Verifies by that currency's inventory count reaching 0.

## Prerequisites

1. Build green (`dotnet build` → 0/0, `$env:exapiPackage` set).
2. Enable **Allow Order Collection** (default off). No other toggle is required — the controller does
   its own verified move + click and self-checks the windows.
3. Open **all three**: Currency Exchange, **stash**, **inventory**.
4. **Block the covered left ~2 inventory columns** (X < 1793 at the probed resolution) with currency
   you are NOT trading (e.g. wisdom scrolls, portal scrolls). This forces collected currency into the
   visible columns. Use F7 (SDK probe) → "Inventory grid" section to see exactly which items read
   `COVERED`.
5. Have at least one **Completed** order to collect.

## 1. Happy path

- Press **F11**.
- Watch the gold HUD line (y≈420): it should walk through "moving/clicking/verifying — <currency> to
  inventory", then "… to stash", ending with:
  `Order collection done: collected N order slot(s), stashed M currency(ies).`
- Verify in game: completed orders cleared; the currency is in your **stash**; inventory only has your
  blockers (plus any currency that couldn't reach stash — see below).

## 2. Partial order with leftover

- If an order partially filled (e.g. offered 4, only 3 sold), it keeps leftover offered currency in
  slot `<5>`. The run should collect **both** the bought currency (`<4>`) and the leftover (`<5>`) for
  that order before it clears. Confirm both the bought currency and the reclaimed offered currency end
  up in stash.

## 3. Covered / unreachable reporting

- Deliberately **don't** block the left columns, or fill the visible columns so currency lands covered.
- Expected: the run still stashes everything it can reach, and the final status names what it couldn't:
  `… N covered/unreachable in inventory (<names>) — unblock the left columns and press F11 again`.
- No blind clicking into the covered region; nothing is lost — the currency is still in inventory.

## 4. Safety / abort paths (each should block or cancel with a clear reason)

- F11 with **Allow Order Collection off** → "enable Allow Order Collection first."
- F11 with the **stash or inventory closed** → "the stash must be open." / "the inventory must be open."
- F11 with the exchange closed or a picker open → blocked.
- **F11 again while running** → aborts the run.
- **Toggle off mid-run** → cancels next tick.
- **Lose PoE foreground** or **area change** mid-run → cancels.
- **Move the mouse** during a tween → cancels ("manual mouse movement detected").
- Nothing to collect (no Completed orders) → completes immediately with "collected 0 order slot(s)…".

## 5. The one behavior to watch closely

The **Phase-1 progress signal** assumes a Completed order's `<4>` icon becomes invisible right after its
ctrl-right-click (and/or the order leaves the list). If in practice the icon does **not** clear promptly,
you'll see the run stall and then:
`Order collection stopped: the completed order slot for <currency> did not clear (inventory full?) …`
If that happens with inventory space free, the verify signal needs adjusting — tell me what the order
row looked like after the click (run F7 to dump the order element) and I'll revise it. Likewise, a
`… did not move to the stash (stash full?)` message on a non-full stash means the Phase-2 signal needs a
look.

## Regression

- F8/F10 (placement / single-hop execution) and F6 (staging) are untouched; collection is a standalone
  controller. Route analysis / inventory sync / gold calibration still block while a collection run is
  in flight (`IsAnyAutomationRunning` includes it).

## Not in scope

Wiring collection into an end-to-end multi-hop loop (step 6). F11 is a standalone, manually-triggered
collect for now.
