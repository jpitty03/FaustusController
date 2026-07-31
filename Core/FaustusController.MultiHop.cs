namespace FaustusController;

public sealed partial class FaustusController
{
    // v2: nested hop audits gained execution-mode, placement-semantics, and ratio-part fields.
    private const int MultiHopRunSchemaVersion = 2;

    private string _multiHopStatus =
        "Press F5 to execute the whole selected route (enable Allow Multi-Hop Execution; " +
        "open exchange + stash + inventory).";
    private bool _multiHopRunPending;
    private int _multiHopRouteRank;

    private bool AreMultiHopPermissionsEnabled() =>
        Settings.AllowMultiHopExecution &&
        AreSingleHopExecutionPermissionsEnabled() &&
        Settings.AllowOrderCollection;

    private void StartMultiHopExecution()
    {
        // Pressing F5 while a chain is in flight aborts it.
        if (_multiHopController.IsRunning)
        {
            CancelMultiHopExecution("Multi-hop execution cancelled by hotkey.");
            return;
        }

        if (!AreMultiHopPermissionsEnabled())
        {
            _multiHopStatus =
                "Multi-hop blocked: enable Allow Multi-Hop Execution plus every single-hop-" +
                "execution and order-collection toggle first.";
            return;
        }

        if (IsAnyAutomationRunning)
        {
            _multiHopStatus = "Multi-hop blocked: another automation is running.";
            return;
        }

        if (_pickerOpenController.IsRunning || _searchQueryController.IsRunning ||
            _cursorTweenController.IsRunning || _selectionController.IsRunning ||
            _amountInputController.IsRunning)
        {
            _multiHopStatus = "Multi-hop blocked: another input operation is running.";
            return;
        }

        var panel = GameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (!panel.IsVisible ||
            !_pickerButtonCalibration.TryResolvePlaceOrder(panel.GetClientRectCache, out _))
        {
            _multiHopStatus =
                "Multi-hop blocked: open the exchange panel, hover Place Order, and press " +
                "F9 to calibrate it first.";
            return;
        }

        var analysis = _lastRouteAnalysis;
        if (analysis == null || analysis.Routes.Count == 0)
        {
            _multiHopStatus = "Multi-hop blocked: run route analysis (Home) first.";
            return;
        }

        var route = analysis.Routes[Math.Clamp(_routeDisplayIndex, 0, analysis.Routes.Count - 1)];
        if (route.Hops.Count == 0)
        {
            _multiHopStatus = "Multi-hop blocked: the selected route has no hops.";
            return;
        }

        var contexts = new List<HopExecutionContext>();
        for (var i = 0; i < route.Hops.Count; i++)
        {
            if (!TryBuildHopContext(i, out var context, out var failure))
            {
                _multiHopStatus = $"Multi-hop blocked: {failure}";
                return;
            }

            // F5 rejects any route containing a resting hop before any input begins: the chain
            // has no fill/cancel lifecycle for resting orders. Place resting hops one at a time
            // with F10 instead.
            if (context.IsResting)
            {
                _multiHopStatus =
                    $"Multi-hop blocked: hop {i + 1} ({context.OfferedCurrency.Name} -> " +
                    $"{context.WantedCurrency.Name}) is a RESTING LIMIT order; F5 executes " +
                    "immediate-only routes. Use F10 for a single resting hop.";
                return;
            }

            contexts.Add(context);
        }

        for (var i = 0; i < contexts.Count - 1; i++)
        {
            if (contexts[i].WantedCurrency.Metadata != contexts[i + 1].OfferedCurrency.Metadata)
            {
                _multiHopStatus =
                    $"Multi-hop blocked: hop {i + 1} output ({contexts[i].WantedCurrency.Name}) " +
                    $"does not fund hop {i + 2} input ({contexts[i + 1].OfferedCurrency.Name}).";
                return;
            }
        }

        if (!_multiHopController.Start(
            GameController,
            contexts,
            contexts[0].Spent,
            analysis.Request.GoldBudget,
            Settings.MaxRateSlippagePercent.Value,
            out var startFailure))
        {
            _multiHopStatus = startFailure;
            return;
        }

        _multiHopRouteRank = route.Rank;
        _multiHopRunPending = true;
        _multiHopStatus = _multiHopController.Status;
    }

    private void TickMultiHopExecution()
    {
        if (_multiHopController.IsRunning && !AreMultiHopPermissionsEnabled())
        {
            CancelMultiHopExecution(
                "Multi-hop cancelled: a required permission toggle was disabled.");
        }
        else
        {
            _multiHopController.Tick(
                GameController,
                _singleHopExecutionController,
                _orderStagingController,
                _orderPlacementController,
                _orderCollectionController,
                _pickerButtonCalibration,
                Settings.CursorTweenSpeed.Value);
        }

        if (_multiHopRunPending &&
            _multiHopController.State is MultiHopState.Completed or MultiHopState.Faulted)
        {
            WriteMultiHopRun();
            _multiHopRunPending = false;
        }

        if (_multiHopController.State != MultiHopState.Idle)
        {
            _multiHopStatus = _multiHopController.Status;
        }
    }

    private void CancelMultiHopExecution(string reason)
    {
        _multiHopController.Cancel(reason);
        // Tear down whatever sub-controller the chain had in flight so nothing lingers.
        _singleHopExecutionController.Cancel(reason);
        CancelOrderPlacement(reason);
        _orderCollectionController.Cancel(reason);
        _multiHopStatus = reason;
    }

    private void WriteMultiHopRun()
    {
        try
        {
            var league = GetCurrentLeague();
            var audits = _multiHopController.Audits.ToList();
            var isCycle = audits.Count > 0 &&
                string.Equals(
                    audits[0].OfferedCurrency.Metadata,
                    audits[^1].WantedCurrency.Metadata,
                    StringComparison.Ordinal);
            var startAmount = _multiHopController.StartAmount;
            var finalReceived = _multiHopController.FinalReceived;
            var file = new MultiHopRunFile
            {
                SchemaVersion = MultiHopRunSchemaVersion,
                ExecutedAtUtc = DateTimeOffset.UtcNow,
                League = league,
                RouteRank = _multiHopRouteRank,
                IsCycle = isCycle,
                PlannedHopCount = _multiHopController.PlannedHopCount,
                CompletedHopCount = _multiHopController.CompletedHopCount,
                Success = _multiHopController.State == MultiHopState.Completed,
                Outcome = _multiHopController.Status,
                TotalGoldCost = _multiHopController.TotalGoldCost,
                StartAmount = startAmount,
                FinalReceived = finalReceived,
                NetGainUnits = isCycle ? finalReceived - startAmount : 0,
                Hops = audits
            };
            var path = Path.Combine(
                ConfigDirectory,
                $"FaustusController_multihop-execution-{SanitizeFileName(league)}.json");
            WriteOrdersJsonAtomically(path, file);
            _multiHopStatus = _multiHopController.Status + $" Run -> {Path.GetFileName(path)}.";
        }
        catch (Exception exception)
        {
            _multiHopStatus = $"Multi-hop run write failed: {exception.Message}";
        }
    }
}
