# ExileAPI Custom Plugin Notes for Claude Code

## Goal

Create a new ExileAPI plugin, tentatively named `FaustusController`, with two responsibilities:

1. Safely generate mouse and keyboard input while Path of Exile is the foreground window.
2. Read and persist Currency Exchange data associated with the Faustus exchange UI.

The optimization target is to accumulate Chaos Orbs or Divine Orbs through whole-unit conversions while minimizing transaction costs. Use `1 Divine Orb = 200 Chaos Orbs` as the configurable valuation baseline, not as a replacement for observed market quotes.

## Current Increment: Bounded Multi-Pair Capture

F3 runs a bounded batch of 1-10 unique directed pairs from the initial `currency -> Chaos` and `currency -> Divine` collection scope. Every pair delegates to the verified F4 one-pair workflow, must produce a fresh stable-rate snapshot, and must persist that snapshot before the batch advances. Pressing F3 again cancels the batch. The implementation deliberately stops before full-plan scanning and path optimization. F4 still runs exactly one current F7 preview pair, and F5-F12 retain the individually testable lower-level operations.

Current source layout:

| File | Responsibility |
| --- | --- |
| `ExchangeRates.cs` | API-independent currency identities, directed pair keys, exact rational rates, whole-unit conversion, snapshots, and latest-rate storage |
| `CurrencyExchangeRateCollector.cs` | Adapter from `CurrencyExchangePanel` public properties into domain snapshots |
| `CurrencyCatalogue.cs` | Deterministic catalogue keyed by `BaseItemType.Metadata`, built from the Currency Exchange DAT wrapper |
| `CurrencyScanPlan.cs` | De-duplicated plan for all directed pairs where Chaos or Divine is one endpoint |
| `CurrencyPickerInspector.cs` | Read-only visible picker-option identity, owned amount, and center-coordinate inspection |
| `CurrencySearchQueryController.cs` | Foreground-gated Ctrl+F, Ctrl+A, token entry, timeout, cancellation, and exact metadata verification |
| `CursorTweenController.cs` | Non-blocking curved cursor interpolation with continuous target/context validation and manual interruption detection |
| `VerifiedOptionSelectionController.cs` | One guarded left click followed by passive selected-item verification with no retry |
| `PickerButtonCalibration.cs` | Manual picker-open transition capture, panel-relative calibration, atomic JSON persistence, and target resolution |
| `CalibratedPickerOpenController.cs` | Curved movement to one calibrated picker button, one click, and expected-side verification without retry |
| `SinglePairScanController.cs` | One-pair orchestration across verified controllers, stable-rate sampling, capture, and hard stop |
| `BoundedScanController.cs` | Bounded initial-scope iteration, per-run freshness/uniqueness guards, persistence handshake, and explicit cancellation |
| `RateCaptureJsonExporter.cs` | Versioned, atomic JSON export of raw and normalized snapshots |
| `FaustusController.cs` | ExileAPI lifecycle, capture hotkey handling, and minimal status rendering |
| `FaustusControllerSettings.cs` | Small user-configurable settings only |

Do not add optimization rules to the collector or UI automation to the rate book. The collector translates live state, the rate book stores observations, and future optimization consumes immutable snapshots.

### Dry-Run Scan Preview

For `N` catalogue currencies, the plan contains `4N - 6` unique directed pairs:

```text
currency -> Chaos
currency -> Divine
Chaos -> currency
Divine -> currency
```

Self-pairs and duplicate Chaos/Divine cross-pairs are removed. Currency order is deterministic by base name then metadata, except that the initial collection scope deliberately places `Orb of Alteration -> Chaos Orb` first as a liquid runtime-test pair. The initial scope contains the `2N - 2` unique pairs whose wanted endpoint is Chaos or Divine. F7 resets the preview to that first Alteration/Chaos pair; successful F3 captures advance the preview through the remaining scope. The reverse `Chaos/Divine -> currency` families remain in the full plan but are not yet automated by F3.

When a picker is open, inspect `CurrencyPicker.Options`, resolve each visible option by `ItemType.Metadata`, and derive its center from `GetClientRectCache`. Retain only immutable identity and coordinate values; never retain the `Element`. Re-inspect every tick while previewing so scrolling and layout changes update the target. The yellow `DRY RUN TARGET` label is visualization only and must not generate input.

The original alphabetical first step, `A Chilling Wind -> Chaos Orb`, frequently had no positive market rate. The runtime-test start was changed to `Orb of Alteration -> Chaos Orb` so stable-rate capture and multi-pair advancement can be exercised with a liquid pair. The preview still clears on area change.

### Search-First Picker Strategy

Using the picker search field is preferred over scrolling because it avoids scroll-position assumptions and should reduce each picker to a small result set. The public `CurrencyExchangeCurrencyPickerElement` has no dedicated search-field property. Available public surfaces are the generic `Element.Children`, `Element.Parent`, `TextNoTags`, visibility, and `GetClientRectCache`.

Runtime testing inspected 513 picker descendants without finding the search field. Neither text nor geometry discovery is reliable through the public element tree and both approaches were removed.

Path of Exile provides a better supported interaction: Ctrl+F automatically focuses the open picker search box. Use that shortcut instead of locating or clicking the field. Runtime testing confirmed that guarded F8 focuses the expected search field, manual queries filter correctly for both picker sides, and modifiers are released.

F8 is the focus-only test hotkey and is guarded by all of the following:

1. `AllowSearchFocusInput` is disabled by default and must be explicitly enabled.
2. A scan preview must already exist from F7.
3. Path of Exile must be the foreground window.
4. The Currency Exchange panel and currency picker must both be visible.
5. Foreground and UI visibility are revalidated after Ctrl is pressed and before F is pressed.
6. F and Ctrl key-up are attempted unconditionally, including failure and cancellation paths.

F8 sends only Ctrl+F. It does not type the query or select an option. The user manually types the cyan status-line query to verify that focus landed in the correct control.

F9 is the query-entry test and has a separate `AllowSearchQueryInput` toggle that defaults to disabled. It executes at most one input action per `Tick` and never sleeps:

```text
WaitForTriggerRelease  wait until the configured F9 hotkey is up, then settle 75 ms
FocusSearch            Ctrl+F
SelectExistingQuery    Ctrl+A
EnterQuery             one lowercase key every 30 ms
WaitForFilteredOption  poll every 50 ms, maximum 3 seconds
Completed              exact ItemType.Metadata is visible
Faulted                foreground, panel, picker side, input, or timeout failure
```

To avoid keyboard-layout-sensitive punctuation, derive the query from the longest lowercase alphanumeric token in the currency name. Examples: `Chaos Orb -> chaos`, `A Chilling Wind -> chilling`, and `Engineer's Orb -> engineer`. A partial query is acceptable only because completion requires an exact metadata match. F9 never clicks the result. Disable `AllowSearchQueryInput`, change areas, close/swap the picker, lose foreground, hold a modifier, or exceed the operation timeout to cancel without further characters. F8 is blocked while automatic query entry is active.

Runtime testing found that the picker search box is focused as soon as the picker opens and remains focused after a query. `HotkeyNodeV2` normally suppresses plugin hotkeys while a text input is focused, which made the first or repeated F9 press appear ineffective until clicking the picker removed text focus. Set `IgnoreFocusedInput = true` on both F8 and F9, enforce it again during plugin initialization for existing settings, and wait for the physical activation key to be released before generating Ctrl+F. No focus-establishing mouse click is required.

Runtime verification confirmed that this focused-input correction allows both the first and repeated F9 query to succeed without clicking the picker.

### Verified Mouse Movement

Runtime testing confirmed that the F10 option center maps to the correct on-screen currency, leaves the picker open, and does not click. Replace teleportation with a non-blocking tween while preserving the same guards.

F10 is a tween-only coordinate test. `AllowVerifiedTargetMouseMove` defaults to disabled. Before moving, require all of the following:

1. F9 completed with an exact metadata match.
2. Path of Exile is foreground.
3. The Currency Exchange panel and picker remain visible.
4. The wanted/offered picker side is unchanged from query verification.
5. The option list is re-read and contains the same exact metadata.
6. The fresh option center lies inside the current picker rectangle.
7. Foreground, panel, picker, and side are revalidated immediately before movement.

Use a cubic Bézier path with smoothstep timing. Duration is distance divided by `CursorTweenSpeed` (default 1600 pixels/second), clamped to 120-650 ms. Offset both control points perpendicular to the direct path by a distance-scaled amount and alternate curve direction between movements. Do not add random jitter.

During every tween tick:

1. Revalidate foreground, panel, picker, picker side, exact metadata, visibility, and picker bounds.
2. Cancel if the fresh target center moved more than 4 pixels from the verified endpoint.
3. Cancel if the actual cursor differs by more than 25 pixels from the last commanded position, treating that as manual user interruption.
4. Calculate one eased Bézier position and call `Input.SetCursorPos` once.
5. Never retain an `Element` or send a mouse button event.

F10 has `IgnoreFocusedInput = true` because the picker search remains focused. Starting a new preview/query, disabling mouse movement, changing areas, or losing context cancels an active tween.

Runtime testing confirmed that the Bézier/smoothstep F10 movement follows the expected curved path, reaches the verified option, respects speed settings, cancels correctly, and does not click.

### Single Verified Option Click

F11 is the first click-enabled increment. `AllowVerifiedOptionClick` defaults to disabled. F11 has `IgnoreFocusedInput = true` and may send exactly one left-button down/up pair only when:

1. F9 completed with an exact metadata target.
2. F10 completed for the same metadata and picker side.
3. Path of Exile is foreground and the panel/picker are visible.
4. The current picker side still matches the query and tween.
5. A fresh picker inspection contains the exact metadata.
6. The fresh center remains within 4 pixels of the completed tween endpoint.
7. The current cursor lies inside the fresh option bounds with a 2-pixel inset.
8. Foreground and picker state are revalidated immediately before mouse down.

Set the mouse-down flag before calling `Input.LeftDown`, and attempt `Input.LeftUp` unconditionally in `finally`. Never hold the button across ticks. After the click, wait up to two seconds for the picker to close and for `WantedItemType` or `OfferedItemType`, according to picker side, to equal the target metadata. Report completion only after both conditions hold. Never retry a timed-out click automatically.

The current and future input state machine uses:

```text
OpenPicker
SendCtrlF
ClearSearch
EnterQuery
WaitForFilteredOptions
VerifyExactMetadata
SelectOption
WaitForSelection
```

Never click the first search result blindly. Re-read `CurrencyPicker.Options` and require an exact `ItemType.Metadata` match before selection.

### Runtime Test Procedure

1. Run `dotnet build Plugins/Source/FaustusController/FaustusController.csproj` with `exapiPackage` set to the ExileAPI distribution root.
2. Reload the source plugin, enable it, and open the Currency Exchange panel with both currencies selected.
3. Press F7 with the picker closed. Confirm the yellow status reports catalogue count, plan step count, the next directed pair, and a prompt to open a picker.
4. Open the `I want` picker. Confirm the yellow status reports that the planned Ctrl+F query is `Chaos Orb`.
5. Leave `AllowSearchQueryInput` disabled and press F9. Confirm the query status says input was blocked and no text changes.
6. Enable `AllowSearchQueryInput`, open the picker, do not click anywhere inside it, and press F9 once.
7. Confirm the search field is replaced with `chaos`, the picker filters, the yellow target marks Chaos Orb, and query status ends with `Verified Chaos Orb by exact metadata; no click sent.`
8. Close the wanted picker, open the `I have` picker, and press F9. Confirm the query becomes `alteration` and status verifies Orb of Alteration by exact metadata.
9. Put another window in the foreground or close the picker during a query. Confirm the operation faults immediately and sends no further characters.
10. Press F, A, and Ctrl normally afterward to confirm no key remains held.
11. Press F9 again while the search field remains focused and without clicking the picker. Confirm the hotkey is detected and Ctrl+A replaces the existing query rather than appending.
12. Move to another area during or after a query. Confirm the query operation and preview clear.
13. Press F6 on a selected pair. Confirm normal capture still updates `config/FaustusController/FaustusController_rate-captures.json` with corrected schema v1 data.

### Mouse Movement Test

1. Complete the F7 and F9 flow until query status reports an exact metadata match and the yellow target label is over that currency.
2. Leave `AllowVerifiedTargetMouseMove` disabled, move the cursor away from the picker, and press F10. Confirm orange status reports the move was blocked and the cursor does not move.
3. Enable `AllowVerifiedTargetMouseMove`, keep Path of Exile foreground, and press F10 once.
4. Confirm the cursor follows a smooth curved path rather than teleporting, finishes at the center of the yellow exact option, orange status reports `no click sent`, the picker remains open, and no currency is selected.
5. Repeat for the opposite picker side and from several starting distances. Confirm longer distances take longer while remaining within the 120-650 ms limits.
6. Replace the query with an unrelated value that hides the verified target, then press F10. Confirm movement is blocked because the exact target is no longer visible.
7. Close the picker or change picker sides before F10. Confirm movement is blocked.
8. Put another application in the foreground and press F10. Confirm no cursor movement occurs outside Path of Exile.
9. Start a longer tween and move the physical mouse away during movement. Confirm the tween cancels instead of fighting the user.
10. Test lower and higher `CursorTweenSpeed` values and confirm movement duration changes without changing the endpoint.
11. Move to another area and confirm the active tween cancels and F10 requires a new F7/F9 verification.

The mouse movement test fails if the cursor teleports, lands outside the yellow target, fights manual movement, sends a mouse button event, closes the picker, selects a currency, uses stale data after the query/side changes, or moves while Path of Exile is not foreground.

### Option Selection Test

1. Complete F7, F9, and F10, and wait until the cursor tween reports completion at the exact yellow option.
2. Leave `AllowVerifiedOptionClick` disabled and press F11. Confirm selection status reports the click was blocked and the picker remains open.
3. Enable `AllowVerifiedOptionClick` and press F11 once without moving the cursor.
4. Confirm exactly one click occurs, the picker closes, the intended wanted/offered currency is selected, and status reports `Verified panel selected <currency> after one click.`
5. Repeat the complete F9/F10/F11 flow for the opposite picker side.
6. Complete F10, move the cursor outside the option manually, and press F11. Confirm the click is blocked.
7. Complete F10, then alter the query so the exact option disappears. Confirm F11 is blocked.
8. Complete F10, then switch picker side or close the picker. Confirm F11 is blocked.
9. Put another application in the foreground and press F11. Confirm no click occurs outside Path of Exile.
10. Press F11 without a completed F10 tween. Confirm no click occurs.
11. If panel selection verification times out, confirm status reports the failure and no second click is sent.

The option selection test fails if F11 works while disabled, clicks without matching F9/F10 identities, clicks outside fresh option bounds, selects the wrong currency, leaves the mouse button held, retries automatically, or sends input outside the foreground visible picker.

Runtime testing confirmed the F11 flow clicks exactly once, closes the picker, selects the correct metadata, and reports successful panel verification.

### Picker-Button Calibration

`CurrencyExchangePanel` does not expose public wanted/offered picker-button elements. Do not traverse hard-coded child indices. F12 instead arms one-time manual calibration:

1. Record whether the picker is currently visible when calibration starts.
2. Wait for a hidden-to-visible picker transition caused by the user's manual click.
3. Require Path of Exile foreground and the cursor inside the panel rectangle.
4. Use `CurrencyPicker.IsPickingWantedCurrency` to classify the click as `I want` or `I have`.
5. Store cursor coordinates normalized to the current panel rectangle.
6. Close the picker and repeat for the other side.
7. Persist each captured side atomically to `config/FaustusController/FaustusController_picker-buttons.json` using schema v1.
8. Render magenta `CALIBRATED I WANT` and `CALIBRATED I HAVE` labels at resolved positions for visual validation.

Calibration sends no input. Starting F12 cancels query, tween, and selection operations. Area changes cancel an armed calibration but retain already saved points. Automatic picker opening must remain disabled until both resolved labels are confirmed over valid click locations.

### Picker Calibration Test

1. Reload the plugin, open Currency Exchange with the picker closed, and press F12.
2. Confirm magenta status says calibration is armed.
3. Manually click `I want` without moving the cursor afterward. Confirm status reports that `I want` was captured.
4. Close the picker, then manually click `I have`. Confirm status reports calibration complete.
5. Close the picker and verify magenta labels align with the exact locations clicked for both buttons.
6. Move the game window or reopen the panel. Confirm labels remain aligned through panel-relative resolution.
7. Inspect `config/FaustusController/FaustusController_picker-buttons.json`. Confirm schema version 1, both normalized points between 0 and 1, and an updated UTC timestamp.
8. Reload the plugin. Confirm both labels are restored from JSON without recalibration.
9. Press F12 while a picker is already open. Confirm status instructs closing it first and no point is captured until the next hidden-to-visible transition.
10. Start calibration, then change areas. Confirm calibration disarms without input.

The calibration test fails if opening one side is classified as the other, a point is outside the panel, labels do not align after panel movement, stale absolute screen coordinates are persisted, or any automated mouse/keyboard input occurs.

Runtime testing confirmed both calibrated labels align with valid `I want` and `I have` click locations, remain aligned after panel/window movement, and reload correctly from schema-v1 JSON.

### One Calibrated Picker Open

F5 is the first automated picker-open increment. `AllowCalibratedPickerOpen` defaults to disabled. It requires an F7 preview, complete F12 calibration, foreground Path of Exile, visible exchange panel, closed picker, and no other running input operation.

Choose exactly one side from current panel state:

```text
if current WantedItemType != planned wanted: open I want
else if current OfferedItemType != planned offered: open I have
else: pair already matches; send no input
```

The `CalibratedPickerOpenController` must:

1. Resolve the calibrated normalized point against the fresh panel rectangle.
2. Tween to it using the same speed, Bézier curve, smoothstep timing, 120-650 ms duration, and manual-interruption behavior as verified option movement.
3. Re-resolve the calibrated point every tick and cancel if it moves more than 4 pixels.
4. Revalidate foreground, visible panel, and closed picker throughout movement.
5. Require the cursor within 6 pixels of the fresh calibrated point before clicking.
6. Send one left-button down/up pair with unconditional release.
7. Wait up to two seconds for the picker to become visible with the expected `IsPickingWantedCurrency` value.
8. Never retry automatically.

Starting a new preview or calibration and changing areas cancel picker opening. F8-F11 are blocked while it runs. Opening a picker clears prior query, cursor, and selection verification because those belonged to the previous picker state.

### Calibrated Picker Open Test

1. Reload complete F12 calibration and verify both magenta labels align.
2. Press F7 and note the planned pair and which current side differs.
3. Leave `AllowCalibratedPickerOpen` disabled and press F5. Confirm no movement or click occurs.
4. Enable `AllowCalibratedPickerOpen`, close the picker, move the cursor away, and press F5 once.
5. Confirm the cursor follows a curved tween to the required calibrated button, clicks once, and opens the expected `I want` or `I have` picker.
6. Confirm magenta status reports `Verified calibrated <side> picker opened after one click.`
7. Complete F9, F10, and F11 to select that planned currency.
8. With the picker closed, press F5 again. Confirm it opens the remaining mismatched side.
9. Select the second currency, then press F5 again. Confirm status says both currencies already match and no input occurs.
10. Start F5 from a longer distance and manually move the mouse during the tween. Confirm cancellation without a click.
11. Close the panel, open a picker manually, lose foreground, or disable the permission during F5. Confirm the operation cancels and never retries.

The picker-open test fails if F5 works while disabled, opens the wrong side, clicks more than once, fights manual movement, retries after timeout, uses a stale panel position, runs concurrently with F8-F11, or sends input outside the foreground visible panel.

Runtime testing confirmed F5 selects the required mismatched side, follows the calibrated tween/click path, verifies the expected picker, and sends no input when both planned currencies already match.

### One Automated Pair

F4 is the first orchestration increment. `AllowSinglePairAutomation` defaults to disabled and all lower-level permission toggles must also be enabled:

```text
AllowCalibratedPickerOpen
AllowSearchQueryInput
AllowVerifiedTargetMouseMove
AllowVerifiedOptionClick
```

F4 requires an F7 preview, complete F12 calibration, foreground Path of Exile, visible exchange panel, closed picker, and no running manual input operation. Its state machine is:

```text
EnsurePair
OpeningPicker
EnteringQuery
MovingToOption
SelectingOption
EnsurePair             repeat once if the other side differs
WaitingForStableRate
Completed              export one snapshot and stop
Faulted                cancel without retry
```

Reuse the existing verified controllers rather than duplicating picker, keyboard, movement, or click logic. Select `I want` first when it differs, otherwise select `I have`. Skip either side that already matches exact metadata.

After both panel item types match:

1. Wait 500 ms before reading rates so pair-dependent UI state can settle.
2. Poll every 100 ms.
3. Require the captured pair metadata to equal the planned directed pair.
4. Require a positive market rate.
5. Require three consecutive identical raw `Get:Give` samples.
6. Store the final immutable snapshot in `ExchangeRateBook` and export through the existing schema-v1 JSON exporter.
7. Stop in `Completed`; never advance to the next plan step.

The overall operation timeout is 30 seconds and the stable-rate timeout is 5 seconds. Losing foreground, changing areas, disabling any required permission, manual mouse interruption, controller failure, or timeout faults the run without automatic retry. Manual F5-F11 operations are blocked while F4 runs. F7 explicitly cancels F4 and creates a new preview.

### Single-Pair Automation Test

1. Reload complete calibration and enable all four lower-level permission toggles.
2. Press F7 and record the exact planned offered/wanted pair.
3. Leave `AllowSinglePairAutomation` disabled and press F4. Confirm no input occurs.
4. Enable `AllowSinglePairAutomation`, close the picker, move the cursor away, and press F4 once.
5. Do not press F5-F11. Confirm F4 automatically opens only each mismatched side, enters the expected query token, verifies metadata, tweens, clicks once, and verifies panel selection.
6. Confirm it skips a side that already matches the planned metadata.
7. After both sides match, confirm status waits 500 ms and reports stable-rate samples `1/3`, `2/3`, and `3/3`.
8. Confirm final green status reports one captured directed pair and explicitly says `stopped`.
9. Inspect `config/FaustusController/FaustusController_rate-captures.json`. Confirm the pair, raw market ratio, stock sides, selected-direction rates, and timestamp match the panel.
10. Confirm F4 does not open another picker or advance to the next F7 plan step after export.
11. Repeat with both sides initially mismatched and with one side already correct.
12. During separate runs, disable one required permission, move the mouse during tweening, close the panel, change areas, and lose foreground. Confirm immediate fault with no retry or further input.
13. Attempt F5-F11 during an active F4 run. Confirm those manual actions are blocked.
14. Test a pair with no positive market rate. Confirm rate capture times out without exporting invalid data.

The single-pair test fails if F4 works while disabled, bypasses a permission, selects incorrect metadata, advances beyond one pair, captures before three stable samples, exports a mismatched/no-rate snapshot, retries after fault, or allows concurrent manual input actions.

### Bounded Multi-Pair Scan

F3 composes the one-pair controller without duplicating picker or input behavior. `AllowBoundedScanAutomation` defaults to disabled, `PairsPerBoundedScan` defaults to 2, and the configured bound is limited to 1-10. All four lower-level permissions required by F4 are also required by F3; `AllowSinglePairAutomation` is independent and is not required for a bounded scan.

F3 starts at the current F7 initial-scope preview and takes at most the configured number of unique steps, wrapping at the end of that scope without repeating a pair in one run. Each run receives an in-memory scan identifier and UTC start time. For every pair:

1. Delegate selection and stable-rate capture to a private `SinglePairScanController`.
2. Require the returned directed pair to equal the current planned pair.
3. Require `CapturedAtUtc` to be at or after the batch start time.
4. Reject duplicate directed pairs within the batch.
5. Store and atomically export the snapshot while the scanner is in `AwaitingPersistence`.
6. Advance only after persistence is confirmed.
7. Wait 500 ms with a foreground, visible-panel, closed-picker guard before starting the next pair.
8. Stop after the configured bound; never continue into an unbounded loop.

The F3 hotkey is a start/cancel toggle while a run is active. F7 cancels the run and rebuilds the preview. F12 cancels it before calibration. Area change, foreground loss, panel/picker mismatch, permission loss, lower-controller failure, stale/mismatched/duplicate capture, or JSON export failure faults the batch without retry. Manual F4-F11 actions are blocked while F3 runs; blocked manual F5/F9/F10/F11 presses do not cancel the shared automated operation.

### Bounded Scan Test

1. Reload the plugin, verify F12 calibration, and enable the four picker/query/movement/click permissions.
2. Set `PairsPerBoundedScan` to 2, leave `AllowBoundedScanAutomation` disabled, press F7, and record the initial-scope preview.
3. Press F3 and confirm no cursor, key, or click input occurs and green status says the bounded scan is blocked.
4. Enable `AllowBoundedScanAutomation`, close the picker, and press F3 once.
5. Confirm status includes a short scan identifier and `pair 1/2`, and the first pair follows the complete verified F4 workflow.
6. Confirm the first snapshot is exported before status changes to the 500 ms between-pair delay.
7. Confirm the second pair starts without another hotkey press, uses the next initial-scope pair, and follows the same exact-metadata and stable-rate checks.
8. Confirm final status reports two fresh persisted pairs and `stopped`, with no third picker opening.
9. Inspect `config/FaustusController/FaustusController_rate-captures.json`; confirm both directed pairs have positive rates and capture timestamps at or after the F3 run start.
10. Set the bound to 1 and confirm F3 behaves as a bounded one-pair batch. Set it to 3 and confirm exactly three unique pairs are exported.
11. Start near the end of the initial scope and confirm wrapping does not duplicate a pair within the same run.
12. During separate runs, press F3 again, press F7, press F12, disable each required permission, move the mouse during a tween, close the panel, open a picker during the between-pair delay, change area, and lose foreground. Confirm immediate cancellation with no retry or subsequent pair.
13. During an active run, press F5 and F9-F11. Confirm each is reported as blocked and the automated run continues undisturbed. Press F4 and F6 and confirm no concurrent scan or manual capture starts.
14. Make the export path unwritable for a controlled test. Confirm the current capture reports export failure and the scanner does not advance. Restore the path afterward.

The bounded-scan test fails if F3 works while disabled, scans a reverse-scope pair, exceeds its configured bound, repeats a pair, accepts a pre-run/mismatched/no-rate snapshot, advances before successful persistence, retries input, or continues after explicit cancellation.

The test fails if F7 generates input, F9 works while disabled, input continues after losing foreground/picker context, any key remains held, the entered token does not replace the old query, completion occurs without exact metadata, an option is clicked, duplicate scan pairs are created, or the plan size differs from `4N - 6`.

### Exact Rate Representation

Never store an exchange rate only as `float` or `double`. Preserve the panel's integer `Get` and `Give` values and reduce them with their greatest common divisor:

```text
wanted units received / offered units spent = Get / Give
```

Examples:

| Display value | Exact executable ratio |
| --- | --- |
| `0.5 chaos` | `1 chaos / 2 source units` |
| `15 chaos` | `15 chaos / 1 source unit` |
| `0.6 divines` | `3 divines / 5 source units` |

`decimal WantedPerOffered` is a derived display/scoring value only. Execution uses the reduced integer ratio. For `available` offered units:

```text
batches = floor(available / GiveUnits)
spent = batches * GiveUnits
received = batches * GetUnits
remainder = available - spent
```

This prevents fractional currency output. Future path comparison should use cross multiplication or another exact rational comparison where rounding could change the winning route.

### Directed Pair Semantics

Every rate is directional:

```text
OfferedCurrency -> WantedCurrency
```

The pair key uses both currencies' `BaseItemType.Metadata`. The reverse direction is a separate observation and must never be inferred by taking the reciprocal because spreads, stock, and transaction costs can differ.

`MarketRateGet` and `MarketRateGive` are selected-pair values: wanted units received and offered units given. Stock rows require side-aware interpretation.

For selected pair `OfferedCurrency -> WantedCurrency`:

| Source | Meaning | Selected-pair rate |
| --- | --- | --- |
| `MarketRateGet/MarketRateGive` | Current market/immediate ratio for the selected direction | `Get / Give` |
| `WantedItemStock` | Existing listings stocked with the currency the user wants; immediately fillable from the selected direction | `RawGet / RawGive` |
| `OfferedItemStock` | Competing inverse listings stocked with the currency the user has | `RawGive / RawGet` for selected-direction comparison only |

Observed example on 2026-07-20:

```text
I want Chaos, I have Divine
Market/immediate ratio:         840 Chaos : 1 Divine
Top wanted-stock row:           raw 840 Chaos : 1 Divine
Top inverse/competing listing: raw 1 Divine : 815 Chaos
Selected-direction comparison: 815 Chaos : 1 Divine

After swapping the panel:
I want Divine, I have Chaos
Market ratio:                   1 Divine : 815 Chaos
```

The 31-pair JSON audit on 2026-07-20 confirmed that `MarketRate` equals the first raw `WantedItemStock` row for every capture. `OfferedItemStock` is the inverse competing book. This spread is meaningful: do not replace the immediate `840:1` quote with the competing `815:1` equivalent, and do not treat the competing equivalent as executable in the selected direction. Swapping the panel turns that inverse listing into the new selected market.

### Baseline Valuation

Keep valuation policy separate from exchange quotes:

```text
Chaos Orb  = 1 chaos-value unit
Divine Orb = 200 chaos-value units
```

The baseline is for comparing route outcomes in a common unit. It must not overwrite a captured Chaos/Divine market edge. Make the value configurable when optimization is introduced so league-specific assumptions can change without changing collected data.

### Optimizer Data Contracts

Introduce optimizer types only after snapshot collection is stable. Keep them API-independent and use integer quantities throughout route execution:

```csharp
public sealed record CurrencyBalance(string Metadata, long Units);

public sealed record TransactionCost(
    long Gold,
    long SourceCurrencyUnits);

public sealed record ConversionStepResult(
    CurrencyPairKey Pair,
    long InputUnits,
    long SpentUnits,
    long OutputUnits,
    long RemainderUnits,
    long GoldCost);

public sealed record ConversionRouteResult(
    IReadOnlyList<ConversionStepResult> Steps,
    long FinalTargetUnits,
    long TotalGoldCost);
```

Do not combine gold and currency into one scalar unless an explicit gold valuation is introduced. Prefer a constrained/lexicographic objective:

1. Reject routes exceeding balances, stock, freshness, hop, or gold-budget constraints.
2. Maximize whole Chaos or Divine units at the target.
3. For equal output, minimize gold cost.
4. For equal output and gold, minimize hops and stranded remainders.

An optimizer edge should reference the immutable snapshot it came from so route results can report quote age and be invalidated before execution. The optimizer should never read `CurrencyExchangePanel` directly.

### Collection Freshness and History

The current `ExchangeRateBook` stores only the latest snapshot per pair in its private `_latestByPair` dictionary. That dictionary exists only in the active plugin process. It is retained across area changes but lost on plugin reload or process exit. A bounded F3 run tracks an in-memory scan identifier, start time, latest capture time, and captured-pair set, but schema v1 does not yet persist the scan identifier.

After every successful capture, export the rate book to:

```text
<ConfigDirectory>/FaustusController_rate-captures.json
```

For this distribution, `ConfigDirectory` resolves to `config/FaustusController`. Schema v1 uses the corrected stock-side direction mapping and includes both currency identities, capture timestamps, market rates, top immediate and competing rates, every raw stock row, selected-pair rates, and listing counts. Export through a temporary file followed by an atomic replacement. Merge with an existing schema-compatible export by directed pair so plugin reloads do not erase previously exported pairs. This is pre-release development, so do not add migration or backward-compatibility code; discard invalid development captures when the schema changes.

A future history increment should add league, scan identifier, and optional failure/staleness reason. Apply a configurable maximum quote age before building graph edges; never silently mix fresh and stale observations from different scans.

Keep each stock row's raw ratio as well as its normalized selected-pair comparison ratio. Do not treat `ListedCount` as available currency volume without validation; its public name supports listing count, not necessarily fillable units.

### Planned Expansion Boundaries

1. Persist versioned snapshots separately from the static currency catalogue.
2. Add a bounded history per directed pair and freshness/league metadata.
3. Completed: add a cancelable bounded scanner that selects by `ItemType.Metadata`, waits for stable rates, persists, then advances.
4. Current scope: scan only `currency -> Chaos` and `currency -> Divine` pairs before attempting the full directed graph.
5. Build a graph where currencies are vertices and fresh executable quotes are directed edges.
6. Add balances, stock/liquidity limits, gold costs, and whole-unit remainders to route simulation.
7. Add bounded path search with cycle prevention and configurable maximum hops.
8. Add dry-run route display before any order placement automation.

The future scanner should use explicit states such as `Idle`, `OpenPicker`, `SelectCurrency`, `WaitForSelection`, `WaitForRate`, `Capture`, `Advance`, `Completed`, and `Faulted`. It must reacquire panel/option elements on every state transition and obey the input safety rules below.

This folder is a compiled ExileAPI distribution, not a source checkout. The public API was determined from the supplied plugin template, dependency manifests, loaded plugins, and public metadata/decompilation of `ExileCore.dll`.

## Important Findings

- The host is `.NET 10`, Windows x64. See `ExileCore.runtimeconfig.json` and `ExileCore.deps.json`.
- The bundled template still targets `net8.0-windows`. Change the generated plugin to `net10.0-windows` before building against this `ExileCore.dll`.
- Source plugins belong in `Plugins/Source/<PluginName>` with the `.csproj` at the top level of that directory.
- Compiled plugins belong in `Plugins/Compiled/<PluginName>`.
- `config/settings.json` currently has `PreferSourcePlugins=false`. Do not leave a compiled plugin with the same identity in `Plugins/Compiled` while developing the source copy, or enable source preference.
- The plugin template defaults `Enable` to `false`; the plugin must be enabled from the ExileAPI menu after it loads.
- There is no dedicated Faustus UI class. Use `GameController.Game.IngameState.IngameUi.CurrencyExchangePanel` directly.
- Currency mappings are available from in-memory game DAT wrappers. Do not scrape display text or hard-code a currency list unless an API field is missing.
- Save mapping collections as custom JSON under `ConfigDirectory`. Complex collections are not good settings-node values; the current logs already show warnings for unsupported complex plugin settings.
- `ExileCore.dll` is obfuscated. Public type/member signatures are usable, but decompiled method bodies and apparent numeric offsets are often invalid. Never copy raw offsets from decompiler output.

## Repository Landmarks

| Path | Purpose |
| --- | --- |
| `Plugins/howto.txt` | Official short plugin creation instructions |
| `Plugins/exApiTools.Plugin.Template.2.0.0.nupkg` | Supplied project template |
| `ExileCore.dll` | Plugin API and game-memory models |
| `GameOffsets.dll` | Current memory offset structures used by the core |
| `ExileCore.runtimeconfig.json` | Confirms `net10.0` runtime |
| `ExileCore.deps.json` | Confirms `win-x64` and host package versions |
| `Plugins/Compiled/Stashie/Stashie.dll` | Existing mouse/keyboard automation example, but uses legacy APIs |
| `Plugins/Compiled/PickIt/PickIt.dll` | Existing cursor/click example, but has local Win32 code and setting bugs |
| `Plugins/Compiled/BuffUtil/BuffUtil.dll` | `SendInput` keyboard example, but lacks a foreground safety check |
| `Plugins/Compiled/BasicFlaskRoutine/BasicFlaskRoutine.dll` | Window-message keyboard example, but contains a broken key-release method |
| `config/global/*_settings.json` | Serialized settings examples |
| `Logs/Info20260720.log` | Confirms bundled plugin discovery/loading |

The `.gitmodules` file points the historical plugin source submodule at:

```text
https://github.com/qvin0000/exileapiplugins
```

## Create the Project

Run these commands from the distribution root:

```powershell
dotnet new install .\Plugins\exApiTools.Plugin.Template.2.0.0.nupkg
dotnet new exApiPlugin -n FaustusController -o .\Plugins\Source\FaustusController
$env:exapiPackage = (Resolve-Path .).Path
```

The generated project contains:

```text
Plugins/Source/FaustusController/
|-- FaustusController.csproj
|-- FaustusController.cs
`-- FaustusControllerSettings.cs
```

Use this corrected project file as the baseline:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <OutputType>Library</OutputType>
    <UseWindowsForms>true</UseWindowsForms>
    <PlatformTarget>x64</PlatformTarget>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>false</SelfContained>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <DebugType>embedded</DebugType>
    <PathMap>$(MSBuildProjectDirectory)=$(MSBuildProjectName)</PathMap>
    <EmbedAllSources>true</EmbedAllSources>
  </PropertyGroup>

  <ItemGroup>
    <Reference Include="ExileCore">
      <HintPath>$(exapiPackage)\ExileCore.dll</HintPath>
      <Private>False</Private>
    </Reference>
    <Reference Include="GameOffsets">
      <HintPath>$(exapiPackage)\GameOffsets.dll</HintPath>
      <Private>False</Private>
    </Reference>
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="ImGui.NET" Version="1.90.0.1" />
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
    <PackageReference Include="SharpDX.Mathematics" Version="4.2.0" />
  </ItemGroup>
</Project>
```

The HUD supplies its own output path when compiling projects under `Plugins/Source`. For a direct compile, run:

```powershell
dotnet build .\Plugins\Source\FaustusController\FaustusController.csproj
```

If the HUD compilation fails, inspect its log and any generated `Errors.txt`. The source compiler uses `Restore` and `Build`, and caches results in a compilation manifest. The ExileAPI menu has a compilation-cache reset if stale output is loaded.

## Plugin Contract

The normal main class is:

```csharp
public sealed class FaustusController
    : BaseSettingsPlugin<FaustusControllerSettings>
```

The settings class must implement `ISettings` and must expose `Enable`:

```csharp
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using System.Windows.Forms;

namespace FaustusController;

public sealed class FaustusControllerSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new(false);
    public ToggleNode RequireForeground { get; set; } = new(true);
    public ToggleNode DryRun { get; set; } = new(true);
    public HotkeyNodeV2 CaptureMappings { get; set; } = new(Keys.F6);
    public HotkeyNodeV2 RunExchangeAction { get; set; } = new(Keys.F7);
    public HotkeyNodeV2 KillSwitch { get; set; } = new(Keys.Pause);
    public HotkeyNodeV2 OutputKey { get; set; } = new(Keys.Space);
    public RangeNode<int> InputDelayMs { get; set; } = new(40, 5, 500);
}
```

Confirmed lifecycle methods on `BaseSettingsPlugin<TSettings>` include:

```csharp
public override void OnLoad();
public override bool Initialise();
public override void AreaChange(AreaInstance area);
public override Job Tick();
public override void Render();
public override void EntityAdded(Entity entity);
public override void EntityRemoved(Entity entity);
public override void ReceiveEvent(string eventId, object args);
public override void OnClose();
public override void OnUnload();
```

Use the lifecycle this way:

| Method | Intended work |
| --- | --- |
| `Initialise` | Build paths, load custom JSON, register/change hotkeys if necessary |
| `AreaChange` | Cancel or reset any pending UI operation and discard stale element references |
| `Tick` | Check state/hotkeys and advance a non-blocking operation |
| `Render` | ImGui and drawing only; never sleep or perform long disk work |
| `OnClose`/`OnUnload` | Cancel operations and save dirty custom data |

`Tick()` and `Render()` are called every frame. Do not use `Thread.Sleep` in either one. Use an explicit state machine, a coroutine/`Job`, or a guarded `Task` with cancellation.

Useful base properties include:

```csharp
GameController GameController
Graphics Graphics
PluginManager PluginManager
string ConfigDirectory
CancellationToken ZoneCancellationToken
```

## Mouse and Keyboard APIs

### Hotkey Detection

Use `HotkeyNodeV2`, not obsolete `HotkeyNode`:

```csharp
if (Settings.CaptureMappings.PressedOnce())
{
    CaptureMappings();
}
```

Confirmed `HotkeyNodeV2` methods:

```csharp
bool PressedOnce();
bool IsPressed();
bool UnpressedOnce();
bool DrawPickerButton(string id);
```

Confirmed constructors include:

```csharp
new HotkeyNodeV2();
new HotkeyNodeV2(Keys key);
new HotkeyNodeV2(HotkeyNodeV2.HotkeyNodeValue value);
```

If hotkey polling does not fire in the first compile, explicitly register the value in `Initialise` and re-register it from `OnValueChanged`:

```csharp
Input.RegisterKey(Settings.CaptureMappings.Value);
Settings.CaptureMappings.OnValueChanged +=
    () => Input.RegisterKey(Settings.CaptureMappings.Value);
```

Do not add this registration speculatively if `PressedOnce()` already works; the current node may be registered by the settings parser.

### Keyboard Generation

Prefer the current helper when sending a configured hotkey value:

```csharp
using ExileCore.Shared.Helpers;

InputHelper.SendInputPress(Settings.OutputKey.Value);
InputHelper.SendInputDown(Settings.OutputKey.Value);
InputHelper.SendInputUp(Settings.OutputKey.Value);
```

All three methods return `bool`. Check the result and log failures.

Legacy static methods also exist:

```csharp
Input.KeyDown(Keys key);
Input.KeyUp(Keys key);
Input.KeyPressRelease(Keys key);
Input.KeyDown(Keys key, nint windowHandle);
Input.KeyUp(Keys key, nint windowHandle);
```

Prefer explicit down/up pairs with `try/finally` for held keys. Do not copy `BasicFlaskRoutine.KeyPressRelease`; that plugin's method presses a key but never releases it.

### Mouse Generation

The newer movement surface is available through `Input.InputManager`:

```csharp
bool moved = Input.InputManager.MoveMouse(screenCoordinate);

await Input.InputManager.MoveMouseAsync(
    stroke,
    cancellationToken);
```

Confirmed manager methods:

```csharp
IStatusDisposable BlockUserMouseInput();
IStatusDisposable BlockUserKeyboardInput();
bool MoveMouse(System.Numerics.Vector2 coordinate);
Task<bool> MoveMouseAsync(
    MouseMoveStroke stroke,
    CancellationToken cancellationToken = default);
SyncTask<bool> MoveMouseSyncTask(
    MouseMoveStroke stroke,
    CancellationToken cancellationToken = default);
```

Movement strokes use:

```csharp
new MouseMoveStroke(new List<MouseMoveStrokePoint>
{
    new(target, TimeSpan.FromMilliseconds(40))
});
```

`MouseMoveStrokePoint` has `Point` and `Delay` values. Confirm the behavior of a one-point stroke in a compile/runtime smoke test before building a humanized multi-point path.

Clicks and wheel actions are on the static API:

```csharp
Input.Click(MouseButtons.Left);
Input.Click(MouseButtons.Right);
Input.LeftDown();
Input.LeftUp();
Input.RightDown();
Input.RightUp();
Input.VerticalScroll(forward: true, clicks: 1);
```

Legacy direct movement also exists:

```csharp
Input.SetCursorPos(System.Numerics.Vector2 position);
```

Prefer `Input.InputManager.MoveMouse` for new code, then use `Input.Click` for the button action.

### Input Safety Rules

Implement all of these before any real exchange automation:

1. Require `GameController.Window.IsForeground()` immediately before every generated input step.
2. Require the expected panel to be visible immediately before every generated input step.
3. Abort on area change, panel close, Escape, plugin disable, or cancellation.
4. Never keep an `Element` reference across an area change; reacquire the panel and child elements.
5. Never hold mouse/keyboard blocking across frames. If blocking is used, wrap the smallest operation in `using` or `try/finally` and inspect `IStatusDisposable.IsSuccess`.
6. Never leave a key or mouse button down after an exception or cancellation.
7. Serialize automation so only one operation can run at a time.
8. Add a dry-run mode that logs/draws target points without generating input.
9. Add a kill-switch hotkey that is independent of the action hotkey.
10. Rate-limit actions and add a short revalidation delay before clicking.

Element rectangles and input movement must use the same coordinate space. Validate this by drawing the intended target point first. The distribution README warns that Windows scaling usually needs to be 100%, so DPI scaling must be tested rather than assumed.

## Currency Exchange API

### Primary Access Paths

```csharp
var ingameState = GameController.Game.IngameState;
var panel = ingameState.IngameUi.CurrencyExchangePanel;
var placedOrders = ingameState.ServerData.PlacedCurrencyExchangeOrders;

var exchangeEntries = GameController.Files.CurrencyExchange.EntriesList;
var exchangeCategories = GameController.Files.CurrencyExchangeCategories.EntriesList;
var currencyItems = GameController.Files.CurrencyItems.EntriesList;
var baseItemTypes = GameController.Files.BaseItemTypes;
```

`UniversalFileWrapper<T>.EntriesList` is the intended way to enumerate DAT records. Do not reach into its private/protected caches.

### Exchange Catalogue Types

`CurrencyExchangeEntry` exposes:

```csharp
BaseItemType BaseItemType { get; }
CurrencyExchangeCategory Category1 { get; }
CurrencyExchangeCategory Category2 { get; }
byte Byte1 { get; }
byte Byte2 { get; }
```

`CurrencyExchangeCategory` exposes:

```csharp
string Name { get; }
string DisplayName { get; }
```

`CurrencyItemDat` exposes:

```csharp
BaseItemType Type { get; }
int StackSize { get; }
string Action { get; }
string Description { get; }
```

`BaseItemType` exposes the durable identity/display fields needed here:

```csharp
string Metadata { get; set; }
string ClassName { get; set; }
string BaseName { get; set; }
uint Hash { get; set; }
```

`BaseItemTypes` provides:

```csharp
Dictionary<string, BaseItemType> Contents { get; }
BaseItemType GetByHash(uint hash);
BaseItemType Translate(string metadata);
```

Use `Metadata` as the persisted primary key. Store `Hash` for fast correlation with current runtime orders, but do not treat the hash as the only durable key across game patches.

### Live Panel Types

`CurrencyExchangePanel` exposes:

```csharp
BaseItemType OfferedItemType { get; }
BaseItemType WantedItemType { get; }
List<CurrencyExchangeStock> WantedItemStock { get; }
List<CurrencyExchangeStock> OfferedItemStock { get; }
short MarketRateGet { get; }
short MarketRateGive { get; }
CurrencyExchangeCurrencyPickerElement CurrencyPicker { get; }
List<CurrencyExchangePanelOrderElement> OrderElements { get; }
List<PlacedCurrencyExchangeOrder> Orders { get; }
Element OfferedItemCountInput { get; }
Element WantedItemCountInput { get; }
Element RatioElement { get; }
```

The picker exposes:

```csharp
bool IsPickingWantedCurrency { get; }
Element OptionContainer { get; }
List<CurrencyExchangeCurrencyPickerCurrencyOption> Options { get; }
```

Each picker option exposes:

```csharp
BaseItemType ItemType { get; }
int Owned { get; }
```

Each stock row exposes:

```csharp
int Get { get; }
int Give { get; }
int ListedCount { get; }
```

Use the panel properties instead of traversing child indexes. The picker currently uses an internal child index, but that is an implementation detail and likely to change.

### Placed Orders

`PlacedCurrencyExchangeOrder` exposes:

```csharp
DateTimeOffset CreationDate { get; }
int PlayerOrderId { get; }
uint OfferedItemHash { get; }
BaseItemType OfferedItemType { get; }
uint WantedItemHash { get; }
BaseItemType WantedItemType { get; }
int OriginalOfferedItemStackSize { get; }
int OfferedItemStackSize { get; }
int WantedItemStackSize { get; }
int GoldCost { get; }
int OfferedItemRatioPart { get; }
int WantedItemRatioPart { get; }
int CompetingOfferedItemRatioPart { get; }
int CompetingWantedItemRatioPart { get; }
bool IsCompleted { get; }
bool IsCanceled { get; }
```

Prefer `OfferedItemType` and `WantedItemType`. If either is unavailable, resolve the hash through `GameController.Files.BaseItemTypes.GetByHash(...)`.

## Mapping Storage Design

Do not put the catalogue in `FaustusControllerSettings`. Keep settings small and use a versioned custom file such as:

```csharp
_mappingPath = Path.Combine(ConfigDirectory, "currency-mappings.json");
```

Recommended schema:

```csharp
public sealed class CurrencyMappingFile
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public List<CurrencyMapping> Items { get; set; } = [];
}

public sealed class CurrencyMapping
{
    public string Metadata { get; set; } = "";
    public uint Hash { get; set; }
    public string BaseName { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string? PrimaryCategory { get; set; }
    public string? PrimaryCategoryDisplayName { get; set; }
    public string? SecondaryCategory { get; set; }
    public string? SecondaryCategoryDisplayName { get; set; }
    public int? StackSize { get; set; }
    public string? Action { get; set; }
    public string? Description { get; set; }
}
```

Keep live observations in a separate file or section because owned amounts, selected items, market stocks, and ratios are transient:

```csharp
public sealed class CurrencyPickerObservation
{
    public DateTimeOffset CapturedAtUtc { get; set; }
    public bool IsPickingWantedCurrency { get; set; }
    public List<CurrencyPickerOptionObservation> Options { get; set; } = [];
}

public sealed class CurrencyPickerOptionObservation
{
    public string Metadata { get; set; } = "";
    public uint Hash { get; set; }
    public string BaseName { get; set; } = "";
    public int Owned { get; set; }
}
```

### Catalogue Join Algorithm

1. Read `CurrencyItems.EntriesList` and index records by `Type.Metadata`.
2. Enumerate `CurrencyExchange.EntriesList`.
3. Skip records whose `BaseItemType` or `BaseItemType.Metadata` is null/empty.
4. Use `BaseItemType.Metadata` as the dictionary key.
5. Copy `Hash`, `BaseName`, and `ClassName` from `BaseItemType`.
6. Copy category names/display names from `Category1` and `Category2` with null checks.
7. Join optional `StackSize`, `Action`, and `Description` from the matching `CurrencyItemDat` record.
8. De-duplicate by metadata and sort deterministically by category, base name, then metadata.
9. Write to a temporary file in `ConfigDirectory`, then atomically replace the destination where practical.
10. Log item count, duplicate count, skipped count, and output path.

JSON save/load can use the host-matching Newtonsoft.Json package:

```csharp
var json = JsonConvert.SerializeObject(data, Formatting.Indented);
File.WriteAllText(_mappingPath, json);

var data = JsonConvert.DeserializeObject<CurrencyMappingFile>(
    File.ReadAllText(_mappingPath));
```

Disk I/O should occur only when a capture is requested, data is dirty, or the plugin closes. Do not serialize every frame.

## Suggested Implementation Order

1. Generate the template project and change it to `net10.0-windows`.
2. Build an empty plugin and confirm it appears in the ExileAPI menu.
3. Add settings and confirm their JSON is written under `config/global`.
4. Add a read-only debug view for the exchange panel and current offered/wanted item names.
5. Enumerate the three DAT wrappers and log counts only.
6. Implement the catalogue join and save `currency-mappings.json`.
7. Add a picker observation capture and save it separately.
8. Add dry-run target visualization for mouse actions.
9. Add one foreground-gated test keypress.
10. Add one foreground/panel-gated test mouse move without clicking.
11. Add clicking only after coordinates and cancellation behavior are verified.
12. Implement the final exchange workflow as a cancelable state machine with revalidation before each input.

## Existing Plugin Review Warnings

Do not blindly copy the bundled automation plugins:

| Plugin | Useful lesson | Problem to avoid |
| --- | --- | --- |
| `Stashie` | Shows cursor movement, clicks, key down/up, scrolling, and hotkeys | Uses obsolete `HotkeyNode` and legacy movement APIs; its `BlockInput` setting is not operationally used |
| `PickIt` | Shows element-targeted cursor/click flow | Uses a local Win32 wrapper; several UI settings are wired incorrectly or never consumed |
| `BuffUtil` | Shows `SendInput` through InputSimulatorCore | Does not verify that Path of Exile is foreground before generating global input |
| `BasicFlaskRoutine` | Shows window-message key input and foreground gating | Its `KeyPressRelease` implementation never sends key-up |

The new plugin should use the core API and should centralize all input in one service/class so every action gets the same foreground, panel, cancellation, and release guarantees.

## Acceptance Criteria

- The project builds against this distribution with .NET 10 and x64.
- The HUD discovers the source plugin without copying `ExileCore.dll` or `GameOffsets.dll` into the plugin folder.
- The plugin starts disabled and exposes configurable action, capture, and kill-switch hotkeys.
- No input is generated when Path of Exile is not foreground.
- No exchange input is generated when `CurrencyExchangePanel` is not visible.
- Area changes and plugin disable cancel pending work and release all held inputs.
- The generated mapping file is versioned, deterministic, and keyed by `BaseItemType.Metadata`.
- The mapping includes current hashes for placed-order correlation but does not use hashes as the only persisted identity.
- Static catalogue data and transient picker/stock/order observations are stored separately.
- The implementation does not hard-code memory offsets or depend on internal UI child indexes.
- The implementation does not block `Tick` or `Render` with sleeps or synchronous long-running work.
- A dry-run mode visualizes intended targets before mouse clicks are enabled.

## API Uncertainties to Resolve by Compiling

The public signatures above are confirmed for this binary, but Claude Code should use short compile checkpoints because the core is obfuscated and this distribution has no matching source tree.

Verify these points in this order:

1. Whether `HotkeyNodeV2.PressedOnce()` works without explicit `Input.RegisterKey`.
2. The exact visibility property inherited by `CurrencyExchangePanel` in this build, normally `IsVisible`.
3. The exact rectangle/center API to use for exchange elements and whether coordinates are already desktop coordinates.
4. Whether a one-point `MouseMoveStroke` delays before or after moving.
5. Whether DAT wrappers are populated during `Initialise`; if not, defer mapping capture until `Tick` sees non-empty `EntriesList` values.

Treat compile errors as API discovery feedback. Do not compensate by adding reflection, hard-coded offsets, or raw process-memory reads unless a required public property is genuinely unavailable.
