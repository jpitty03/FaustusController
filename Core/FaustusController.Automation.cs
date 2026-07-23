using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using Newtonsoft.Json;
using System.Windows.Forms;
using Vector2 = System.Numerics.Vector2;

namespace FaustusController;

public sealed partial class FaustusController
{
    private void StartSinglePairAutomation()
    {
        if (_boundedScanController.IsRunning || _liquidityDiscoveryController.IsRunning)
        {
            _singlePairScanController.Cancel(
                "Single-pair scan blocked: another automated scan is running.");
            return;
        }

        if (!Settings.AllowSinglePairAutomation)
        {
            _singlePairScanController.Cancel(
                "Single-pair scan blocked: enable Allow Single Pair Automation first.");
            return;
        }

        if (!AreSinglePairPermissionsEnabled())
        {
            _singlePairScanController.Cancel(
                "Single-pair scan blocked: enable all picker, query, movement, and click permissions.");
            return;
        }

        if (_previewStep == null || _catalogue == null)
        {
            _singlePairScanController.Cancel(
                "Single-pair scan blocked: press F7 to create a plan preview first.");
            return;
        }

        if (!_pickerButtonCalibration.IsComplete)
        {
            _singlePairScanController.Cancel(
                "Single-pair scan blocked: complete F12 calibration first.");
            return;
        }

        if (_pickerOpenController.IsRunning || _searchQueryController.IsRunning ||
            _cursorTweenController.IsRunning || _selectionController.IsRunning)
        {
            _singlePairScanController.Cancel(
                "Single-pair scan blocked: another input operation is running.");
            return;
        }

        _pickerOpenController.Cancel("Picker opener reset for single-pair scan.");
        _searchQueryController.Cancel("Query controller reset for single-pair scan.");
        _cursorTweenController.Cancel("Cursor tween reset for single-pair scan.");
        _selectionController.Cancel("Selection controller reset for single-pair scan.");
        _singlePairScanController.Start(GameController, _previewStep, out _);
    }

    private void ToggleBoundedScanAutomation()
    {
        if (_boundedScanController.IsRunning)
        {
            CancelBoundedScanAutomation("Bounded scan cancelled by F3; no retry sent.");
            return;
        }

        if (!Settings.AllowBoundedScanAutomation)
        {
            _boundedScanController.Block(
                "Bounded scan blocked: enable Allow Bounded Scan Automation first.");
            return;
        }

        if (!AreBoundedScanPermissionsEnabled())
        {
            _boundedScanController.Block(
                "Bounded scan blocked: enable all picker, query, movement, and click permissions.");
            return;
        }

        if (_singlePairScanController.IsRunning)
        {
            _boundedScanController.Block(
                "Bounded scan blocked: single-pair scan is running.");
            return;
        }

        if (_liquidityDiscoveryController.IsRunning)
        {
            _boundedScanController.Block(
                "Bounded scan blocked: liquidity discovery is running.");
            return;
        }

        if (_previewStep == null || _catalogue == null || _scanPlan == null)
        {
            _boundedScanController.Block(
                "Bounded scan blocked: press F7 to create an initial-scope preview first.");
            return;
        }

        if (!_pickerButtonCalibration.IsComplete)
        {
            _boundedScanController.Block(
                "Bounded scan blocked: complete F12 calibration first.");
            return;
        }

        if (_pickerOpenController.IsRunning || _searchQueryController.IsRunning ||
            _cursorTweenController.IsRunning || _selectionController.IsRunning)
        {
            _boundedScanController.Block(
                "Bounded scan blocked: another input operation is running.");
            return;
        }

        _pickerOpenController.Cancel("Picker opener reset for bounded scan.");
        _searchQueryController.Cancel("Query controller reset for bounded scan.");
        _cursorTweenController.Cancel("Cursor tween reset for bounded scan.");
        _selectionController.Cancel("Selection controller reset for bounded scan.");
        if (_boundedScanController.Start(
            GameController,
            _scanPlan.InitialCollectionSteps,
            _previewStep,
            Settings.PairsPerBoundedScan.Value,
            _collectorSessionId,
            out _))
        {
            PersistBoundedScanProgressIfChanged();
        }
    }

    private void ToggleLiquidityDiscoveryAutomation()
    {
        if (_liquidityDiscoveryController.IsRunning)
        {
            if (_activeRefreshRun)
            {
                _inputStatus = "F2 discovery blocked: Insert active refresh is running.";
                return;
            }

            CancelLiquidityDiscovery("Liquidity discovery cancelled by F2; no retry sent.");
            return;
        }

        if (!Settings.AllowLiquidityDiscoveryAutomation)
        {
            _liquidityDiscoveryController.Block(
                "Liquidity discovery blocked: enable Allow Liquidity Discovery Automation first.");
            return;
        }

        if (!AreLiquidityDiscoveryPermissionsEnabled())
        {
            _liquidityDiscoveryController.Block(
                "Liquidity discovery blocked: enable all picker, query, movement, and click permissions.");
            return;
        }

        StartLiquidityAutomation(activeRefresh: false);
    }

    private void ToggleActiveRefreshAutomation()
    {
        if (_liquidityDiscoveryController.IsRunning)
        {
            if (!_activeRefreshRun)
            {
                _inputStatus = "Insert active refresh blocked: F2 discovery is running.";
                return;
            }

            CancelLiquidityDiscovery("Active refresh cancelled by Insert; no retry sent.");
            return;
        }

        if (!Settings.AllowActiveRefreshAutomation)
        {
            _liquidityDiscoveryController.Block(
                "Active refresh blocked: enable Allow Active Refresh Automation first.");
            return;
        }

        if (!AreActiveRefreshPermissionsEnabled())
        {
            _liquidityDiscoveryController.Block(
                "Active refresh blocked: enable all picker, query, movement, and click permissions.");
            return;
        }

        StartLiquidityAutomation(activeRefresh: true);
    }

    private void StartLiquidityAutomation(bool activeRefresh)
    {
        var operation = activeRefresh ? "Active refresh" : "Liquidity discovery";

        if (_singlePairScanController.IsRunning || _boundedScanController.IsRunning)
        {
            _liquidityDiscoveryController.Block(
                $"{operation} blocked: another automated scan is running.");
            return;
        }

        if (_catalogue == null)
        {
            _liquidityDiscoveryController.Block(
                $"{operation} blocked: the live catalogue is not available yet.");
            return;
        }

        if (_scanPlan == null &&
            !CurrencyScanPlan.TryCreate(_catalogue, out _scanPlan, out var planFailure))
        {
            _liquidityDiscoveryController.Block(
                $"{operation} blocked: {planFailure}");
            return;
        }

        if (!_pickerButtonCalibration.IsComplete)
        {
            _liquidityDiscoveryController.Block(
                $"{operation} blocked: complete F12 calibration first.");
            return;
        }

        if (_pickerOpenController.IsRunning || _searchQueryController.IsRunning ||
            _cursorTweenController.IsRunning || _selectionController.IsRunning)
        {
            _liquidityDiscoveryController.Block(
                $"{operation} blocked: another input operation is running.");
            return;
        }

        if (!EnsureDiscoveryOverrides(forceReload: true) ||
            !EnsureDiscoveryProbeRegistry())
        {
            _liquidityDiscoveryController.Block(
                $"{operation} blocked: probe registry or manual overrides could not be loaded.");
            return;
        }

        var plan = activeRefresh
            ? GetActiveRefreshRunPlan()
            : GetLiquidityDiscoveryRunPlan();
        if (activeRefresh)
        {
            if (!TryExportActiveRefreshPlan(plan, out var refreshFailure))
            {
                _liquidityDiscoveryController.Block(
                    $"Active refresh blocked: {refreshFailure}");
                return;
            }
        }
        else if (plan.Steps.Count == 0)
        {
            _liquidityDiscoveryController.Block(
                "Full discovery is complete. Use Insert for directed active refresh.");
            if (!_activeRefreshRun || !_liquidityDiscoveryController.IsRunning)
            {
                _ = TryExportActiveRefreshPlan(GetActiveRefreshRunPlan(), out _);
            }
            return;
        }

        if (plan.Steps.Count == 0)
        {
            _liquidityDiscoveryController.Block(
                "Active refresh blocked: the directed refresh plan contains no pairs.");
            return;
        }

        _pickerOpenController.Cancel($"Picker opener reset for {operation.ToLowerInvariant()}.");
        _searchQueryController.Cancel($"Query controller reset for {operation.ToLowerInvariant()}.");
        _cursorTweenController.Cancel($"Cursor tween reset for {operation.ToLowerInvariant()}.");
        _selectionController.Cancel($"Selection controller reset for {operation.ToLowerInvariant()}.");
        if (_liquidityDiscoveryController.Start(
            GameController,
            plan.Steps,
            out _,
            plan.Label))
        {
            _activeRefreshRun = activeRefresh;
        }
    }

    private bool AreSinglePairPermissionsEnabled()
    {
        return Settings.AllowSinglePairAutomation &&
            Settings.AllowCalibratedPickerOpen &&
            Settings.AllowSearchQueryInput &&
            Settings.AllowVerifiedTargetMouseMove &&
            Settings.AllowVerifiedOptionClick;
    }

    private bool AreBoundedScanPermissionsEnabled()
    {
        return Settings.AllowBoundedScanAutomation &&
            Settings.AllowCalibratedPickerOpen &&
            Settings.AllowSearchQueryInput &&
            Settings.AllowVerifiedTargetMouseMove &&
            Settings.AllowVerifiedOptionClick;
    }

    private bool AreLiquidityDiscoveryPermissionsEnabled()
    {
        return Settings.AllowLiquidityDiscoveryAutomation &&
            Settings.AllowCalibratedPickerOpen &&
            Settings.AllowSearchQueryInput &&
            Settings.AllowVerifiedTargetMouseMove &&
            Settings.AllowVerifiedOptionClick;
    }

    private bool AreActiveRefreshPermissionsEnabled()
    {
        return Settings.AllowActiveRefreshAutomation &&
            Settings.AllowCalibratedPickerOpen &&
            Settings.AllowSearchQueryInput &&
            Settings.AllowVerifiedTargetMouseMove &&
            Settings.AllowVerifiedOptionClick;
    }

    private bool IsAnyAutomationRunning =>
        _singlePairScanController.IsRunning ||
        _boundedScanController.IsRunning ||
        _liquidityDiscoveryController.IsRunning;

    private void CancelSinglePairAutomation(string reason)
    {
        if (!_singlePairScanController.IsRunning)
        {
            return;
        }

        _singlePairScanController.Cancel(reason);
        CancelSharedInputControllers(reason);
    }

    private void CancelBoundedScanAutomation(string reason)
    {
        if (!_boundedScanController.IsRunning)
        {
            return;
        }

        _boundedScanController.Cancel(reason);
        CancelSharedInputControllers(reason);
    }

    private void CancelLiquidityDiscovery(string reason)
    {
        if (!_liquidityDiscoveryController.IsRunning)
        {
            return;
        }

        _liquidityDiscoveryController.Cancel(reason);
        CancelSharedInputControllers(reason);
    }

    private void CancelSharedInputControllers(string reason)
    {
        _pickerOpenController.Cancel(reason);
        _searchQueryController.Cancel(reason);
        _cursorTweenController.Cancel(reason);
        _selectionController.Cancel(reason);
    }

    private bool StoreAndExportAutomatedSnapshot(
        ExchangePairSnapshot snapshot,
        ExchangeCaptureSource captureSource,
        string sourceLabel,
        BoundedScanProgress? boundedScanProgress = null)
    {
        var contextualSnapshot = AttachCaptureContext(snapshot, captureSource);
        _rateBook.Store(contextualSnapshot);
        _captureStatus = $"{sourceLabel} capture " +
            $"{contextualSnapshot.OfferedCurrency.Name} -> " +
            $"{contextualSnapshot.WantedCurrency.Name} in {contextualSnapshot.League}: " +
            $"{FormatRatio(contextualSnapshot.MarketRate)}.";
        try
        {
            if (boundedScanProgress != null)
            {
                _lastAttemptedBoundedScanRevision = _boundedScanController.Revision;
                _nextBoundedScanManifestRetryUtc = DateTimeOffset.UtcNow +
                    TimeSpan.FromSeconds(2);
            }

            var exportResult = _exporter.Export(
                _rateBook.LatestSnapshots,
                _exportPath,
                boundedScanProgress,
                _collectorSessionId);
            if (boundedScanProgress != null)
            {
                _lastPersistedBoundedScanRevision = _boundedScanController.Revision;
            }

            _exportStatus = FormatExportStatus(exportResult);
            return RefreshMarketDiscovery();
        }
        catch (Exception exception)
        {
            _exportStatus = $"Rate export failed: {exception.Message}";
            return false;
        }
    }

    private void PersistBoundedScanProgressIfChanged()
    {
        var progress = _boundedScanController.GetProgress();
        var revision = _boundedScanController.Revision;
        if (progress == null || revision == _lastPersistedBoundedScanRevision ||
            (revision == _lastAttemptedBoundedScanRevision &&
                DateTimeOffset.UtcNow < _nextBoundedScanManifestRetryUtc))
        {
            return;
        }

        _lastAttemptedBoundedScanRevision = revision;
        _nextBoundedScanManifestRetryUtc = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        try
        {
            var exportResult = _exporter.Export(
                _rateBook.LatestSnapshots,
                _exportPath,
                progress,
                _collectorSessionId);
            _lastPersistedBoundedScanRevision = revision;
            _exportStatus = FormatExportStatus(exportResult);
            _conversionGraphDirty = true;
            _nextConversionGraphRetryUtc = DateTimeOffset.UtcNow;
        }
        catch (Exception exception)
        {
            _exportStatus = $"Scan manifest export failed: {exception.Message}";
            if (_boundedScanController.State != BoundedScanState.Faulted)
            {
                var reason = "Bounded scan faulted because its manifest could not be persisted.";
                _boundedScanController.FailPersistence(reason);
                CancelSharedInputControllers(reason);
                PersistBoundedScanProgressIfChanged();
            }
        }
    }
}
