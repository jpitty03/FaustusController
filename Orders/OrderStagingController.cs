using ExileCore;
using System.Windows.Forms;

namespace FaustusController;

public enum OrderStagingState
{
    Idle,
    SelectingPair,
    TypingOfferedAmount,
    TypingWantedAmount,
    LockingInAmounts,
    Staged,
    Faulted
}

/// <summary>
/// Stages one currency-exchange order as a DRY RUN: selects the pair through the
/// verified single-pair machinery, checks listed stock when the staged ratio should
/// fill immediately, types both verified amounts, then presses Enter once so both
/// amount inputs lose focus and the game enables Place Order, and finally re-captures
/// the live stock to confirm the order is still available. It still never clicks
/// the place-order button; that final step is a separate, later feature with its
/// own toggle. The fresh pair capture is exposed via TakeStagedSnapshot so the host
/// can persist it exactly like an Insert-triggered active refresh.
/// </summary>
public sealed class OrderStagingController
{
    private static readonly TimeSpan LockInTimeout = TimeSpan.FromSeconds(2);

    private readonly SinglePairScanController _pairController = new();
    private CurrencyScanPlanStep? _step;
    private long _offeredAmount;
    private long _wantedAmount;
    private string _league = "";
    private ExchangePairSnapshot? _stagedSnapshot;
    private DateTimeOffset _lockInDeadlineUtc;
    private bool _lockInKeySent;

    public OrderStagingState State { get; private set; } = OrderStagingState.Idle;
    public string Status { get; private set; } =
        "Order staging (dry run) is disabled by default.";
    public bool IsRunning => State is OrderStagingState.SelectingPair or
        OrderStagingState.TypingOfferedAmount or
        OrderStagingState.TypingWantedAmount or
        OrderStagingState.LockingInAmounts;

    // Staged order details, read by OrderPlacementController once State is Staged.
    public CurrencyScanPlanStep? CurrentStep => _step;
    public long OfferedAmount => _offeredAmount;
    public long WantedAmount => _wantedAmount;

    public bool Start(
        GameController gameController,
        CurrencyScanPlanStep step,
        long offeredAmount,
        long wantedAmount,
        out string failureReason)
    {
        if (IsRunning)
        {
            return FailStart("Order staging is already running.", out failureReason);
        }

        if (offeredAmount <= 0 || wantedAmount <= 0)
        {
            return FailStart(
                "Order staging blocked: the selected hop has non-positive amounts.",
                out failureReason);
        }

        if (!gameController.Window.IsForeground())
        {
            return FailStart(
                "Order staging blocked: Path of Exile is not foreground.",
                out failureReason);
        }

        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (!panel.IsVisible || panel.CurrencyPicker.IsVisible)
        {
            return FailStart(
                "Order staging blocked: the exchange panel must be visible with no open picker.",
                out failureReason);
        }

        if (!_pairController.Start(gameController, step, out var pairFailure))
        {
            return FailStart($"Order staging pair selection failed to start: {pairFailure}",
                out failureReason);
        }

        _step = step;
        _offeredAmount = offeredAmount;
        _wantedAmount = wantedAmount;
        _league = gameController.Game.IngameState.ServerData.League;
        _stagedSnapshot = null;
        _lockInKeySent = false;
        State = OrderStagingState.SelectingPair;
        Status = $"Staging order (dry run): {step.OfferedCurrency.Name} " +
            $"{offeredAmount} -> {step.WantedCurrency.Name} {wantedAmount}.";
        failureReason = string.Empty;
        return true;
    }

    public void Tick(
        GameController gameController,
        CurrencyCatalogue catalogue,
        PickerButtonCalibrationController calibration,
        CalibratedPickerOpenController pickerOpenController,
        CurrencyPickerInspector pickerInspector,
        CurrencySearchQueryController queryController,
        CursorTweenController tweenController,
        VerifiedOptionSelectionController selectionController,
        CurrencyExchangeRateCollector rateCollector,
        CurrencyAmountInputController amountController,
        int cursorSpeed)
    {
        if (!IsRunning)
        {
            return;
        }

        var liveLeague = gameController.Game.IngameState.ServerData.League;
        if (!string.Equals(liveLeague, _league, StringComparison.Ordinal))
        {
            Cancel("Order staging cancelled: live league changed mid-run.");
            return;
        }

        if (!gameController.Window.IsForeground())
        {
            Cancel("Order staging cancelled: Path of Exile lost foreground.");
            return;
        }

        switch (State)
        {
            case OrderStagingState.SelectingPair:
                TickSelectingPair(
                    gameController,
                    catalogue,
                    calibration,
                    pickerOpenController,
                    pickerInspector,
                    queryController,
                    tweenController,
                    selectionController,
                    rateCollector,
                    amountController,
                    cursorSpeed);
                return;
            case OrderStagingState.TypingOfferedAmount:
                // The amount controller is ticked by the plugin's main loop; this
                // state machine only observes its verified terminal states.
                if (amountController.State == CurrencyAmountInputState.Faulted)
                {
                    Cancel($"Order staging failed while typing the offered amount: {amountController.Status}");
                    return;
                }

                if (amountController.State != CurrencyAmountInputState.Completed)
                {
                    Status = $"Staging offered amount: {amountController.Status}";
                    return;
                }

                if (!VerifyPairStillSelected(gameController, out var pairFailure))
                {
                    Cancel($"Order staging cancelled before the wanted amount: {pairFailure}");
                    return;
                }

                if (!amountController.StartAutomated(
                    gameController,
                    wantedInput: true,
                    _wantedAmount,
                    cursorSpeed,
                    out var wantedFailure))
                {
                    Cancel($"Order staging wanted-amount input failed to start: {wantedFailure}");
                    return;
                }

                State = OrderStagingState.TypingWantedAmount;
                Status = $"Offered amount verified; typing wanted amount {_wantedAmount}.";
                return;
            case OrderStagingState.TypingWantedAmount:
                if (amountController.State == CurrencyAmountInputState.Faulted)
                {
                    Cancel($"Order staging failed while typing the wanted amount: {amountController.Status}");
                    return;
                }

                if (amountController.State != CurrencyAmountInputState.Completed)
                {
                    Status = $"Staging wanted amount: {amountController.Status}";
                    return;
                }

                if (!VerifyPairStillSelected(gameController, out var finalFailure))
                {
                    Cancel($"Order staging completed typing but final verification failed: {finalFailure}");
                    return;
                }

                State = OrderStagingState.LockingInAmounts;
                _lockInKeySent = false;
                _lockInDeadlineUtc = DateTimeOffset.UtcNow + LockInTimeout;
                Status = "Both amounts typed; locking them in so Place Order unlocks.";
                return;
            case OrderStagingState.LockingInAmounts:
                TickLockingInAmounts(gameController, rateCollector);
                return;
        }
    }

    public void Cancel(string reason)
    {
        if (_pairController.IsRunning)
        {
            _pairController.Cancel(reason);
        }

        State = OrderStagingState.Faulted;
        Status = reason;
    }

    private void TickSelectingPair(
        GameController gameController,
        CurrencyCatalogue catalogue,
        PickerButtonCalibrationController calibration,
        CalibratedPickerOpenController pickerOpenController,
        CurrencyPickerInspector pickerInspector,
        CurrencySearchQueryController queryController,
        CursorTweenController tweenController,
        VerifiedOptionSelectionController selectionController,
        CurrencyExchangeRateCollector rateCollector,
        CurrencyAmountInputController amountController,
        int cursorSpeed)
    {
        _pairController.Tick(
            gameController,
            catalogue,
            calibration,
            pickerOpenController,
            pickerInspector,
            queryController,
            tweenController,
            selectionController,
            rateCollector,
            cursorSpeed);

        if (_pairController.State == SinglePairScanState.Faulted)
        {
            Cancel($"Order staging failed during pair selection: {_pairController.Status}");
            return;
        }

        var snapshot = _pairController.TakeCapturedSnapshot();
        if (snapshot == null)
        {
            Status = $"Staging pair: {_pairController.Status}";
            return;
        }

        // Expose the fresh capture so the host stores and exports it exactly like
        // an Insert-triggered active refresh, even when staging stops below.
        _stagedSnapshot = snapshot;

        if (!VerifyPairStillSelected(gameController, out var verifyFailure))
        {
            Cancel($"Order staging rejected the selected pair: {verifyFailure}");
            return;
        }

        if (!TryValidateImmediateStock(snapshot, out var stockFailure))
        {
            Cancel($"Order staging blocked: {stockFailure}");
            return;
        }

        if (!amountController.StartAutomated(
            gameController,
            wantedInput: false,
            _offeredAmount,
            cursorSpeed,
            out var offeredFailure))
        {
            Cancel($"Order staging offered-amount input failed to start: {offeredFailure}");
            return;
        }

        State = OrderStagingState.TypingOfferedAmount;
        Status = $"Pair verified with a live market rate; typing offered amount {_offeredAmount}.";
    }

    private bool VerifyPairStillSelected(
        GameController gameController,
        out string failureReason)
    {
        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (!panel.IsVisible)
        {
            failureReason = "the exchange panel is no longer visible.";
            return false;
        }

        if (panel.CurrencyPicker.IsVisible)
        {
            failureReason = "a currency picker is unexpectedly open.";
            return false;
        }

        var offeredMatches =
            panel.OfferedItemType?.Metadata == _step!.OfferedCurrency.Metadata;
        var wantedMatches =
            panel.WantedItemType?.Metadata == _step.WantedCurrency.Metadata;
        if (!offeredMatches || !wantedMatches)
        {
            failureReason = "the panel's selected currencies no longer match the staged pair.";
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    public ExchangePairSnapshot? TakeStagedSnapshot()
    {
        var snapshot = _stagedSnapshot;
        _stagedSnapshot = null;
        return snapshot;
    }

    private bool TryValidateImmediateStock(
        ExchangePairSnapshot snapshot,
        out string failureReason)
    {
        var topRate = snapshot.TopImmediateRate;
        if (topRate == null)
        {
            failureReason = string.Empty;
            return true;
        }

        // The staged trade should fill immediately when it asks for no more wanted
        // units per offered unit than the best listed ratio provides.
        var expectsImmediateFill =
            (long)topRate.GetUnits * _offeredAmount >=
            (long)topRate.GiveUnits * _wantedAmount;
        if (!expectsImmediateFill)
        {
            failureReason = string.Empty;
            return true;
        }

        long listedAtOrBetter = 0;
        foreach (var level in snapshot.WantedItemStock)
        {
            var rate = level.SelectedPairRate;
            if ((long)rate.GetUnits * _offeredAmount >=
                (long)rate.GiveUnits * _wantedAmount)
            {
                listedAtOrBetter += level.ListedCount;
            }
        }

        if (listedAtOrBetter >= _wantedAmount)
        {
            failureReason = string.Empty;
            return true;
        }

        failureReason = $"this trade should fill immediately, but only {listedAtOrBetter} " +
            $"{_step!.WantedCurrency.Name} is listed at the staged ratio or better " +
            $"({_wantedAmount} wanted); the fresh market capture was still recorded.";
        return false;
    }

    private void TickLockingInAmounts(
        GameController gameController,
        CurrencyExchangeRateCollector rateCollector)
    {
        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (!panel.IsVisible || panel.CurrencyPicker.IsVisible)
        {
            Cancel("Order staging lock-in cancelled: the exchange panel changed.");
            return;
        }

        var offeredInput = panel.OfferedItemCountInput;
        var wantedInput = panel.WantedItemCountInput;
        var now = DateTimeOffset.UtcNow;
        if (!_lockInKeySent)
        {
            if (offeredInput.IsActive || wantedInput.IsActive)
            {
                if (Input.IsKeyDown(Keys.ControlKey) || Input.IsKeyDown(Keys.ShiftKey) ||
                    Input.IsKeyDown(Keys.Menu))
                {
                    Cancel("Order staging lock-in cancelled: release Ctrl, Shift, and Alt.");
                    return;
                }

                var keyDown = false;
                try
                {
                    keyDown = true;
                    Input.KeyDown(Keys.Enter);
                }
                finally
                {
                    if (keyDown)
                    {
                        Input.KeyUp(Keys.Enter);
                    }
                }
            }

            _lockInKeySent = true;
            _lockInDeadlineUtc = now + LockInTimeout;
            Status = "Pressed Enter once; waiting for both amount inputs to lose focus.";
            return;
        }

        var stillActive = offeredInput.IsActive || wantedInput.IsActive;
        if (stillActive && now < _lockInDeadlineUtc)
        {
            Status = "Waiting for the amount inputs to lose focus.";
            return;
        }

        // A count input's IsActive flag can stay latched in memory even after the
        // game really defocused it (the enabled Place Order button is not readable
        // through the SDK), so past the deadline the typed digits, the pair, and a
        // fresh market re-capture are what decide.
        var offeredDigits = CurrencyAmountInputController.ReadDigits(offeredInput);
        var wantedDigits = CurrencyAmountInputController.ReadDigits(wantedInput);
        if (offeredDigits != _offeredAmount.ToString() ||
            wantedDigits != _wantedAmount.ToString())
        {
            Cancel("Order staging lock-in failed: the amounts changed while losing focus " +
                $"(have '{offeredDigits}', want '{wantedDigits}').");
            return;
        }

        if (!VerifyPairStillSelected(gameController, out var pairFailure))
        {
            Cancel($"Order staging stopped after filling the form: {pairFailure}");
            return;
        }

        // Re-capture the live stock so the staged order is verified against the
        // market as it exists right now; the host persists this capture exactly
        // like an Insert-triggered active refresh, keeping listings current.
        if (!rateCollector.TryCaptureCurrentPair(
            gameController,
            out var finalSnapshot,
            out var captureFailure))
        {
            Cancel($"Order staging stopped: the final market re-check failed: {captureFailure}");
            return;
        }

        _stagedSnapshot = finalSnapshot;
        if (!TryValidateImmediateStock(finalSnapshot!, out var finalStockFailure))
        {
            Cancel($"Order staging stopped: the order is no longer available — {finalStockFailure}");
            return;
        }

        State = OrderStagingState.Staged;
        Status = $"DRY RUN staged: {_step!.OfferedCurrency.Name} {_offeredAmount} -> " +
            $"{_step.WantedCurrency.Name} {_wantedAmount}; amounts locked in, the " +
            "immediate fill is still listed, and Place Order is yours to click" +
            (stillActive ? " (an input flag stayed latched in memory)." : ".");
    }

    private bool FailStart(string reason, out string failureReason)
    {
        State = OrderStagingState.Faulted;
        Status = reason;
        failureReason = reason;
        return false;
    }
}
