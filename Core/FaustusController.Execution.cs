namespace FaustusController;

public sealed partial class FaustusController
{
    private string _hopExecutionStatus =
        "Press F10 to execute the selected route's first hop " +
        "(enable Allow Single-Hop Execution).";
    private string _hopExecutionPath = "";

    private bool AreSingleHopExecutionPermissionsEnabled() =>
        Settings.AllowSingleHopExecution && AreOrderPlacementPermissionsEnabled();

    private void StartSingleHopExecution()
    {
        // Pressing F10 while a run is in flight aborts it.
        if (_singleHopExecutionController.IsRunning)
        {
            CancelSingleHopExecution("Single-hop execution cancelled by hotkey.");
            return;
        }

        if (!AreSingleHopExecutionPermissionsEnabled())
        {
            _hopExecutionStatus =
                "Single-hop execution blocked: enable Allow Single-Hop Execution plus every " +
                "order-placement toggle first.";
            return;
        }

        if (IsAnyAutomationRunning)
        {
            _hopExecutionStatus = "Single-hop execution blocked: another automation is running.";
            return;
        }

        if (_pickerOpenController.IsRunning || _searchQueryController.IsRunning ||
            _cursorTweenController.IsRunning || _selectionController.IsRunning ||
            _amountInputController.IsRunning)
        {
            _hopExecutionStatus =
                "Single-hop execution blocked: another input operation is running.";
            return;
        }

        var panel = GameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (!panel.IsVisible ||
            !_pickerButtonCalibration.TryResolvePlaceOrder(panel.GetClientRectCache, out _))
        {
            _hopExecutionStatus =
                "Single-hop execution blocked: open the exchange panel, hover Place Order, " +
                "and press F9 to calibrate it first.";
            return;
        }

        if (!TryBuildHopContext(0, out var context, out var failure))
        {
            _hopExecutionStatus = $"Single-hop execution blocked: {failure}";
            return;
        }

        // A standalone hop's floor is its own planned rate less the allowed slippage:
        // don't trade worse than the analysis assumed (a better live rate still
        // recomputes up and proceeds).
        var plannedRate = (double)context.Received / context.Spent *
            (1.0 - Settings.MaxRateSlippagePercent.Value / 100.0);
        if (!_singleHopExecutionController.Start(
            GameController,
            _orderStagingController,
            context.Step,
            context.OfferedCurrency,
            context.WantedCurrency,
            context.Spent,
            context.Received,
            context.GiveUnitsPerLot,
            context.GetUnitsPerLot,
            plannedRate,
            out var startFailure))
        {
            _hopExecutionStatus = startFailure;
            return;
        }

        _hopExecutionStatus = _singleHopExecutionController.Status;
    }

    private void TickSingleHopExecution()
    {
        // Execution needs one more toggle than placement, so the placement
        // permission guard cannot catch AllowSingleHopExecution being flipped off.
        if (_singleHopExecutionController.IsRunning &&
            !AreSingleHopExecutionPermissionsEnabled())
        {
            CancelSingleHopExecution(
                "Single-hop execution cancelled: a required permission toggle was disabled.");
            return;
        }

        _singleHopExecutionController.Tick(
            GameController,
            _orderStagingController,
            _orderPlacementController,
            _pickerButtonCalibration);

        var audit = _singleHopExecutionController.TakePendingAudit();
        if (audit != null)
        {
            WriteHopExecutionAudit(audit);
            return;
        }

        // Reflect live and faulted status; leave the initial prompt untouched before
        // a run, and preserve the "Audit -> file" line written on the Completed frame.
        if (_singleHopExecutionController.State is not SingleHopExecutionState.Idle
            and not SingleHopExecutionState.Completed)
        {
            _hopExecutionStatus = _singleHopExecutionController.Status;
        }
    }

    private void CancelSingleHopExecution(string reason)
    {
        _singleHopExecutionController.Cancel(reason);
        // Also stop any staging/placement the coordinator started.
        CancelOrderPlacement(reason);
        _hopExecutionStatus = reason;
    }

    private void WriteHopExecutionAudit(HopExecutionAuditFile audit)
    {
        try
        {
            _hopExecutionPath = Path.Combine(
                ConfigDirectory,
                $"FaustusController_hop-execution-{SanitizeFileName(audit.League)}.json");
            WriteOrdersJsonAtomically(_hopExecutionPath, audit);
            _hopExecutionStatus = _singleHopExecutionController.Status +
                $" Audit -> {Path.GetFileName(_hopExecutionPath)}.";
        }
        catch (Exception exception)
        {
            _hopExecutionStatus = $"Single-hop execution audit write failed: {exception.Message}";
        }
    }
}
