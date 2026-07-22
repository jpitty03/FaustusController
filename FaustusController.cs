using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using Newtonsoft.Json;
using System.Windows.Forms;
using Vector2 = System.Numerics.Vector2;

namespace FaustusController;

public sealed class FaustusController : BaseSettingsPlugin<FaustusControllerSettings>
{
    private readonly CurrencyExchangeRateCollector _collector = new();
    private readonly CurrencyCatalogueBuilder _catalogueBuilder = new();
    private readonly CurrencyPickerInspector _pickerInspector = new();
    private readonly CurrencySearchQueryController _searchQueryController = new();
    private readonly CursorTweenController _cursorTweenController = new();
    private readonly VerifiedOptionSelectionController _selectionController = new();
    private readonly PickerButtonCalibrationController _pickerButtonCalibration = new();
    private readonly PickerButtonCalibrationStore _pickerButtonCalibrationStore = new();
    private readonly CalibratedPickerOpenController _pickerOpenController = new();
    private readonly SinglePairScanController _singlePairScanController = new();
    private readonly BoundedScanController _boundedScanController = new();
    private readonly LiquidityDiscoveryController _liquidityDiscoveryController = new();
    private readonly CurrencyDiscoveryProbeStore _discoveryProbeStore = new();
    private readonly CurrencyDiscoveryOverrideStore _discoveryOverrideStore = new();
    private readonly ActiveRefreshPlanExporter _activeRefreshPlanExporter = new();
    private readonly ExchangeRateBook _rateBook = new();
    private readonly RateCaptureJsonExporter _exporter = new();
    private readonly ActiveMarketDiscoveryExporter _marketDiscoveryExporter = new();
    private readonly CurrencyConversionGraphExporter _conversionGraphExporter = new();
    private readonly CurrencyRouteAnalyzer _routeAnalyzer = new();
    private readonly Guid _collectorSessionId = Guid.NewGuid();
    private long _lastAttemptedBoundedScanRevision = -1;
    private long _lastPersistedBoundedScanRevision = -1;
    private DateTimeOffset _nextBoundedScanManifestRetryUtc;
    private bool _marketDiscoveryDirty;
    private DateTimeOffset _nextMarketDiscoveryRetryUtc;
    private bool _conversionGraphDirty;
    private DateTimeOffset _nextConversionGraphRetryUtc;
    private DateTimeOffset _nextConversionGraphExpirationUtc = DateTimeOffset.MaxValue;
    private int _lastConversionGraphMaximumAgeMinutes = -1;
    private DateTimeOffset _nextDiscoveryCatalogueAttemptUtc;
    private CurrencyCatalogue? _catalogue;
    private CurrencyScanPlan? _scanPlan;
    private CurrencyScanPlanStep? _previewStep;
    private CurrencyPickerOptionTarget? _previewTarget;
    private string _captureStatus = "Use the capture hotkey with an exchange pair selected.";
    private string _exportPath = "";
    private string _pickerButtonCalibrationPath = "";
    private string _marketDiscoveryPath = "";
    private string _conversionGraphPath = "";
    private string _activeRefreshPlanPath = "";
    private string _routeRequestPath = "";
    private string _routeAnalysisPath = "";
    private bool _activeRefreshPlanDirty;
    private DateTimeOffset _nextActiveRefreshPlanRetryUtc;
    private string _discoveryProbePath = "";
    private string _discoveryProbeLeague = "";
    private Dictionary<CurrencyPairKey, CurrencyDiscoveryProbeOutcome> _discoveryProbeOutcomes = [];
    private string _discoveryOverridePath = "";
    private string _discoveryOverrideLeague = "";
    private CurrencyDiscoveryOverrides _discoveryOverrides = CurrencyDiscoveryOverrides.Empty;
    private bool _activeRefreshRun;
    private string _exportStatus = "";
    private string _marketDiscoveryStatus = "Active-market discovery is waiting for the live catalogue.";
    private string _conversionGraphStatus = "Conversion graph is waiting for the live catalogue.";
    private string _activeRefreshStatus = "Active refresh plan is waiting for discovery.";
    private string _routeAnalysisStatus = "Press Home to create/run exact route analysis.";
    private CurrencyRouteAnalysisFile? _lastRouteAnalysis;
    private string _scanStatus = "Press F7 to build and preview the next scan step.";
    private string _inputStatus = "Search focus input is disabled by default.";

    public override bool Initialise()
    {
        Settings.FocusPickerSearch.IgnoreFocusedInput = true;
        Settings.EnterPickerSearchQuery.IgnoreFocusedInput = true;
        Settings.MoveToVerifiedOption.IgnoreFocusedInput = true;
        Settings.SelectVerifiedOption.IgnoreFocusedInput = true;
        Settings.CalibratePickerButtons.IgnoreFocusedInput = true;
        Settings.OpenNextPlannedPicker.IgnoreFocusedInput = true;
        Settings.RunSinglePairAutomation.IgnoreFocusedInput = true;
        Settings.RunBoundedScanAutomation.IgnoreFocusedInput = true;
        Settings.RunLiquidityDiscoveryAutomation.IgnoreFocusedInput = true;
        Settings.RunActiveRefreshAutomation.IgnoreFocusedInput = true;
        Settings.RunRouteAnalysis.IgnoreFocusedInput = true;
        _exportPath = Path.Combine(ConfigDirectory, "FaustusController_rate-captures.json");
        _pickerButtonCalibrationPath = Path.Combine(
            ConfigDirectory,
            "FaustusController_picker-buttons.json");
        _marketDiscoveryPath = Path.Combine(
            ConfigDirectory,
            "FaustusController_active-markets.json");
        _conversionGraphPath = Path.Combine(
            ConfigDirectory,
            "FaustusController_conversion-graph.json");
        if (File.Exists(_exportPath))
        {
            try
            {
                var exportResult = _exporter.Export(
                    [],
                    _exportPath,
                    activeCollectorSessionId: _collectorSessionId);
                foreach (var snapshot in _exporter.LoadSnapshots(_exportPath))
                {
                    _rateBook.Store(snapshot);
                }

                _exportStatus = $"Validated schema-v4 latest captures: " +
                    $"{exportResult.CaptureCount} league/pair captures loaded at {_exportPath}";
            }
            catch (Exception exception)
            {
                _exportStatus = $"Rate export validation failed: {exception.Message}";
            }
        }
        else
        {
            _exportStatus = $"Capture export: {_exportPath}";
        }

        try
        {
            var calibration = _pickerButtonCalibrationStore.Load(
                _pickerButtonCalibrationPath);
            if (calibration != null)
            {
                _pickerButtonCalibration.Load(calibration);
            }
        }
        catch (Exception exception)
        {
            _pickerButtonCalibration.Cancel(
                $"Picker-button calibration load failed: {exception.Message}");
        }

        return true;
    }

    public override void AreaChange(AreaInstance area)
    {
        _previewStep = null;
        _previewTarget = null;
        _inputStatus = "Search focus preview cleared after area change.";
        _cursorTweenController.Cancel("Cursor tween cancelled after area change.");
        _selectionController.Cancel("Option selection verification cancelled after area change.");
        _pickerButtonCalibration.Cancel("Picker-button calibration cancelled after area change.");
        _pickerOpenController.Cancel("Picker-open operation cancelled after area change.");
        _singlePairScanController.Cancel("Single-pair scan cancelled after area change.");
        _boundedScanController.Cancel("Bounded scan cancelled after area change.");
        _liquidityDiscoveryController.Cancel("Liquidity discovery cancelled after area change.");
        _searchQueryController.Cancel("Search query cancelled after area change.");
        _marketDiscoveryDirty = true;
        _nextMarketDiscoveryRetryUtc = DateTimeOffset.UtcNow;
        _conversionGraphDirty = true;
        _nextConversionGraphRetryUtc = DateTimeOffset.UtcNow;
        _captureStatus = "Area changed; captured rate snapshots were retained.";
        _scanStatus = "Dry-run preview cleared after area change.";
    }

    public override Job Tick()
    {
        EnsureMarketDiscoveryCatalogue();

        if (Settings.CaptureCurrentRate.PressedOnce())
        {
            if (IsAnyAutomationRunning)
            {
                _captureStatus = "Manual capture blocked: automated scan is running.";
            }
            else
            {
                if (_collector.TryCaptureCurrentPair(
                    GameController,
                    out var snapshot,
                    out var failureReason))
                {
                    var contextualSnapshot = AttachCaptureContext(
                        snapshot!,
                        ExchangeCaptureSource.Manual);
                    _rateBook.Store(contextualSnapshot);
                    var immediateStock = contextualSnapshot.TopImmediateStock;
                    var rawImmediate = immediateStock == null
                        ? ""
                        : $" (raw wanted {immediateStock.RawGet}:{immediateStock.RawGive})";
                    var competingStock = contextualSnapshot.TopCompetingStock;
                    var rawCompeting = competingStock == null
                        ? ""
                        : $" (raw opposite {competingStock.RawGet}:{competingStock.RawGive})";
                    _captureStatus = $"Captured {contextualSnapshot.OfferedCurrency.Name} -> " +
                        $"{contextualSnapshot.WantedCurrency.Name} in " +
                        $"{contextualSnapshot.League}. Market: " +
                        $"{FormatRatio(contextualSnapshot.MarketRate)}; immediate: " +
                        $"{FormatRatio(contextualSnapshot.TopImmediateRate)}{rawImmediate}; " +
                        $"competing: {FormatRatio(contextualSnapshot.TopCompetingRate)}{rawCompeting}.";

                    try
                    {
                        var exportResult = _exporter.Export(
                            _rateBook.LatestSnapshots,
                            _exportPath,
                            activeCollectorSessionId: _collectorSessionId);
                        _exportStatus = FormatExportStatus(exportResult);
                        if (!RefreshMarketDiscovery())
                        {
                            _captureStatus +=
                                " Active-market regeneration failed and will retry.";
                        }
                    }
                    catch (Exception exception)
                    {
                        _exportStatus = $"Rate export failed: {exception.Message}";
                    }
                }
                else
                {
                    _captureStatus = failureReason;
                }
            }
        }

        if (Settings.PreviewNextScanStep.PressedOnce())
        {
            StartDryRunPreview();
        }

        if (Settings.RunBoundedScanAutomation.PressedOnce())
        {
            ToggleBoundedScanAutomation();
        }

        if (Settings.RunLiquidityDiscoveryAutomation.PressedOnce())
        {
            ToggleLiquidityDiscoveryAutomation();
        }

        if (Settings.RunActiveRefreshAutomation.PressedOnce())
        {
            ToggleActiveRefreshAutomation();
        }

        if (Settings.RunRouteAnalysis.PressedOnce())
        {
            RunRouteAnalysis();
        }

        if (Settings.RunSinglePairAutomation.PressedOnce())
        {
            StartSinglePairAutomation();
        }

        if (_previewStep != null)
        {
            UpdateDryRunTarget();
        }

        if (Settings.FocusPickerSearch.PressedOnce())
        {
            FocusPickerSearch();
        }

        if (Settings.EnterPickerSearchQuery.PressedOnce())
        {
            StartSearchQuery();
        }

        if (Settings.MoveToVerifiedOption.PressedOnce())
        {
            MoveToVerifiedOption();
        }

        if (Settings.SelectVerifiedOption.PressedOnce())
        {
            SelectVerifiedOption();
        }

        if (Settings.CalibratePickerButtons.PressedOnce())
        {
            StartPickerButtonCalibration();
        }

        if (Settings.OpenNextPlannedPicker.PressedOnce())
        {
            OpenNextPlannedPicker();
        }

        if (_singlePairScanController.IsRunning && !AreSinglePairPermissionsEnabled())
        {
            CancelSinglePairAutomation(
                "Single-pair scan cancelled: one or more required permission toggles were disabled.");
        }

        if (_boundedScanController.IsRunning && !AreBoundedScanPermissionsEnabled())
        {
            CancelBoundedScanAutomation(
                "Bounded scan cancelled: one or more required permission toggles were disabled.");
        }

        if (_liquidityDiscoveryController.IsRunning &&
            !(_activeRefreshRun
                ? AreActiveRefreshPermissionsEnabled()
                : AreLiquidityDiscoveryPermissionsEnabled()))
        {
            CancelLiquidityDiscovery(
                $"{(_activeRefreshRun ? "Active refresh" : "Liquidity discovery")} cancelled: " +
                    "one or more required permission toggles were disabled.");
        }

        if (_catalogue != null)
        {
            if (_searchQueryController.IsRunning && !Settings.AllowSearchQueryInput)
            {
                _searchQueryController.Cancel(
                    "Search query cancelled: Allow Search Query Input was disabled.");
            }
            else
            {
                _searchQueryController.Tick(
                    GameController,
                    _pickerInspector,
                    _catalogue);
            }

            if (_cursorTweenController.IsRunning &&
                !Settings.AllowVerifiedTargetMouseMove)
            {
                _cursorTweenController.Cancel(
                    "Cursor tween cancelled: Allow Verified Target Mouse Move was disabled.");
            }
            else
            {
                _cursorTweenController.Tick(
                    GameController,
                    _pickerInspector,
                    _catalogue);
            }
        }

        _selectionController.Tick(GameController);
        if (_pickerOpenController.IsRunning && !Settings.AllowCalibratedPickerOpen)
        {
            _pickerOpenController.Cancel(
                "Picker open cancelled: Allow Calibrated Picker Open was disabled.");
        }
        else
        {
            _pickerOpenController.Tick(GameController, _pickerButtonCalibration);
        }

        _pickerButtonCalibration.Tick(GameController);
        if (_pickerButtonCalibration.IsDirty)
        {
            try
            {
                _pickerButtonCalibrationStore.Save(
                    _pickerButtonCalibration.Data,
                    _pickerButtonCalibrationPath);
                _pickerButtonCalibration.MarkSaved();
            }
            catch (Exception exception)
            {
                _pickerButtonCalibration.Cancel(
                    $"Picker-button calibration save failed: {exception.Message}");
            }
        }

        if (_catalogue != null)
        {
            _singlePairScanController.Tick(
                GameController,
                _catalogue,
                _pickerButtonCalibration,
                _pickerOpenController,
                _pickerInspector,
                _searchQueryController,
                _cursorTweenController,
                _selectionController,
                _collector,
                Settings.CursorTweenSpeed.Value);
        }

        var automatedSnapshot = _singlePairScanController.TakeCapturedSnapshot();
        if (automatedSnapshot != null)
        {
            if (!StoreAndExportAutomatedSnapshot(
                automatedSnapshot,
                ExchangeCaptureSource.SinglePairAutomation,
                "Single-pair"))
            {
                _singlePairScanController.Cancel(
                    "Single-pair capture persistence failed; no retry sent.");
            }
        }

        var boundedScanWasRunning = _boundedScanController.IsRunning;
        if (_catalogue != null)
        {
            _boundedScanController.Tick(
                GameController,
                _catalogue,
                _pickerButtonCalibration,
                _pickerOpenController,
                _pickerInspector,
                _searchQueryController,
                _cursorTweenController,
                _selectionController,
                _collector,
                Settings.CursorTweenSpeed.Value);
        }

        if (boundedScanWasRunning &&
            _boundedScanController.State == BoundedScanState.Faulted)
        {
            CancelSharedInputControllers(_boundedScanController.Status);
        }

        var boundedSnapshot = _boundedScanController.PendingSnapshot;
        if (boundedSnapshot != null)
        {
            if (StoreAndExportAutomatedSnapshot(
                boundedSnapshot,
                ExchangeCaptureSource.BoundedScanAutomation,
                "Bounded scan",
                _boundedScanController.GetProgress()))
            {
                if (!_boundedScanController.ConfirmSnapshotPersisted(out var confirmationFailure))
                {
                    CancelBoundedScanAutomation(confirmationFailure);
                }
                else if (_scanPlan != null)
                {
                    _previewStep = _scanPlan.GetNextInitialCollectionStep(boundedSnapshot.Pair);
                    _scanStatus = _previewStep == null
                        ? "The initial collection scan plan is empty."
                        : FormatScanStep(_previewStep);
                }
            }
            else
            {
                CancelBoundedScanAutomation(
                    "Bounded scan cancelled because its capture could not be persisted.");
            }
        }

        if (_catalogue != null)
        {
            _liquidityDiscoveryController.Tick(
                GameController,
                _catalogue,
                _pickerButtonCalibration,
                _pickerOpenController,
                _pickerInspector,
                _searchQueryController,
                _cursorTweenController,
                _selectionController,
                _collector,
                Settings.CursorTweenSpeed.Value);
        }

        var pendingProbe = _liquidityDiscoveryController.PendingProbe;
        if (pendingProbe != null && TryPersistDiscoveryProbe(pendingProbe))
        {
            if (!_liquidityDiscoveryController.ConfirmProbePersisted(out var probeFailure))
            {
                CancelLiquidityDiscovery(probeFailure);
            }
        }

        if (_activeRefreshRun && !_liquidityDiscoveryController.IsRunning &&
            _liquidityDiscoveryController.State is LiquidityDiscoveryState.Completed or
                LiquidityDiscoveryState.Faulted)
        {
            _activeRefreshRun = false;
            _ = TryExportActiveRefreshPlan(GetActiveRefreshRunPlan(), out _);
        }

        PersistBoundedScanProgressIfChanged();
        if (_marketDiscoveryDirty && _catalogue != null &&
            DateTimeOffset.UtcNow >= _nextMarketDiscoveryRetryUtc)
        {
            _ = RefreshMarketDiscovery();
        }

        if (_activeRefreshPlanDirty && !_activeRefreshRun && _catalogue != null &&
            _scanPlan != null && DateTimeOffset.UtcNow >= _nextActiveRefreshPlanRetryUtc &&
            EnsureDiscoveryProbeRegistry())
        {
            _ = TryExportActiveRefreshPlan(GetActiveRefreshRunPlan(), out _);
        }

        if (_catalogue != null &&
            !_conversionGraphDirty &&
            _lastConversionGraphMaximumAgeMinutes != Settings.MaximumQuoteAgeMinutes.Value)
        {
            _conversionGraphDirty = true;
            _nextConversionGraphRetryUtc = DateTimeOffset.UtcNow;
        }

        if (!_conversionGraphDirty && _catalogue != null &&
            DateTimeOffset.UtcNow >= _nextConversionGraphExpirationUtc)
        {
            _conversionGraphDirty = true;
            _nextConversionGraphRetryUtc = DateTimeOffset.UtcNow;
        }

        if (_conversionGraphDirty && _catalogue != null &&
            DateTimeOffset.UtcNow >= _nextConversionGraphRetryUtc)
        {
            _ = RefreshConversionGraph();
        }

        return null!;
    }

    private void StartDryRunPreview()
    {
        if (_liquidityDiscoveryController.IsRunning)
        {
            _inputStatus = "F7 preview blocked: liquidity discovery is running.";
            return;
        }

        CancelSinglePairAutomation("Single-pair scan cancelled by a new preview.");
        CancelBoundedScanAutomation("Bounded scan cancelled by a new preview.");
        CancelLiquidityDiscovery("Liquidity discovery cancelled by a new preview.");
        _searchQueryController.Cancel("Search query cancelled by a new scan preview.");
        _cursorTweenController.Cancel("Cursor tween cancelled by a new scan preview.");
        _selectionController.Cancel("Option selection cancelled by a new scan preview.");
        _pickerOpenController.Cancel("Picker-open operation cancelled by a new scan preview.");
        _previewTarget = null;
        _inputStatus = "F8 can focus search after Allow Search Focus Input is enabled.";
        if (!_catalogueBuilder.TryBuild(GameController, out _catalogue, out var catalogueFailure))
        {
            _scanStatus = catalogueFailure;
            return;
        }

        if (!CurrencyScanPlan.TryCreate(_catalogue!, out _scanPlan, out var planFailure))
        {
            _scanStatus = planFailure;
            return;
        }

        EnsureDiscoveryProbeRegistry();

        _previewStep = _scanPlan!.InitialCollectionSteps.FirstOrDefault();
        if (_previewStep == null)
        {
            _scanStatus = "The generated scan plan is empty.";
            return;
        }

        _scanStatus = FormatScanStep(_previewStep);
        _ = RefreshMarketDiscovery();
    }

    private void UpdateDryRunTarget()
    {
        _previewTarget = null;
        if (_previewStep == null || _catalogue == null || _scanPlan == null)
        {
            return;
        }

        if (!_pickerInspector.TryInspect(
            GameController,
            _catalogue,
            out var inspection,
            out var inspectionFailure))
        {
            _scanStatus = $"{FormatScanStep(_previewStep)} {inspectionFailure}";
            return;
        }

        var desiredCurrency = inspection!.IsPickingWantedCurrency
            ? _previewStep.WantedCurrency
            : _previewStep.OfferedCurrency;
        var searchStatus = $"Ctrl+F query will be '{desiredCurrency.Name}'.";
        _previewTarget = inspection.VisibleOptions.FirstOrDefault(
            option => option.Currency.Metadata == desiredCurrency.Metadata);
        if (_previewTarget == null)
        {
            var side = inspection.IsPickingWantedCurrency ? "wanted" : "offered";
            _scanStatus = $"{FormatScanStep(_previewStep)} {searchStatus} " +
                $"Target {desiredCurrency.Name} is not visible in the {side} picker " +
                $"({inspection.VisibleOptions.Count} visible options).";
            return;
        }

        _scanStatus = $"{FormatScanStep(_previewStep)} {searchStatus} " +
            $"{(inspection.IsPickingWantedCurrency ? "Wanted" : "Offered")} target " +
            $"{desiredCurrency.Name} at " +
            $"({_previewTarget.Center.X:0}, {_previewTarget.Center.Y:0}); " +
            $"owned {_previewTarget.Owned}.";
    }

    private void FocusPickerSearch()
    {
        if (IsAnyAutomationRunning)
        {
            _inputStatus = "Ctrl+F blocked: automated scan is running.";
            return;
        }

        if (_pickerOpenController.IsRunning)
        {
            _inputStatus = "Ctrl+F blocked: picker-open operation is running.";
            return;
        }

        if (_cursorTweenController.IsRunning)
        {
            _inputStatus = "Ctrl+F blocked: cursor tween is running.";
            return;
        }

        if (_selectionController.IsRunning)
        {
            _inputStatus = "Ctrl+F blocked: option selection verification is running.";
            return;
        }

        if (_searchQueryController.IsRunning)
        {
            _inputStatus = "Ctrl+F blocked: automatic query input is running.";
            return;
        }

        if (!Settings.AllowSearchFocusInput)
        {
            _inputStatus = "Ctrl+F blocked: enable Allow Search Focus Input first.";
            return;
        }

        if (_previewStep == null)
        {
            _inputStatus = "Ctrl+F blocked: press F7 to create a scan preview first.";
            return;
        }

        if (!GameController.Window.IsForeground())
        {
            _inputStatus = "Ctrl+F blocked: Path of Exile is not foreground.";
            return;
        }

        var panel = GameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (!panel.IsVisible || !panel.CurrencyPicker.IsVisible)
        {
            _inputStatus = "Ctrl+F blocked: the Currency Exchange picker is not visible.";
            return;
        }

        var desiredCurrency = panel.CurrencyPicker.IsPickingWantedCurrency
            ? _previewStep.WantedCurrency
            : _previewStep.OfferedCurrency;
        var controlDown = false;
        var fDown = false;
        try
        {
            controlDown = true;
            Input.KeyDown(Keys.ControlKey);
            if (!GameController.Window.IsForeground() || !panel.IsVisible ||
                !panel.CurrencyPicker.IsVisible)
            {
                _inputStatus = "Ctrl+F aborted after Ctrl: UI focus changed.";
                return;
            }

            fDown = true;
            Input.KeyDown(Keys.F);
            _inputStatus = $"Sent Ctrl+F. Type '{desiredCurrency.Name}' manually to verify focus.";
        }
        catch (Exception exception)
        {
            _inputStatus = $"Ctrl+F failed: {exception.Message}";
        }
        finally
        {
            Exception? releaseFailure = null;
            if (fDown)
            {
                try
                {
                    Input.KeyUp(Keys.F);
                }
                catch (Exception exception)
                {
                    releaseFailure = exception;
                }
            }

            if (controlDown)
            {
                try
                {
                    Input.KeyUp(Keys.ControlKey);
                }
                catch (Exception exception)
                {
                    releaseFailure ??= exception;
                }
            }

            if (releaseFailure != null)
            {
                _inputStatus = $"Key release failed: {releaseFailure.Message}";
            }
        }
    }

    private void StartSearchQuery()
    {
        if (IsAnyAutomationRunning)
        {
            _inputStatus = "Query input blocked: automated scan is running.";
            return;
        }

        if (_pickerOpenController.IsRunning)
        {
            _searchQueryController.Cancel(
                "Query input blocked: picker-open operation is running.");
            return;
        }

        _selectionController.Cancel("Option selection cancelled by query input.");
        if (_cursorTweenController.IsRunning)
        {
            _cursorTweenController.Cancel("Cursor tween cancelled by query input.");
        }

        if (!Settings.AllowSearchQueryInput)
        {
            _searchQueryController.Cancel(
                "Query input blocked: enable Allow Search Query Input first.");
            return;
        }

        if (_previewStep == null || _catalogue == null)
        {
            _searchQueryController.Cancel(
                "Query input blocked: press F7 to create a scan preview first.");
            return;
        }

        if (!GameController.Window.IsForeground())
        {
            _searchQueryController.Cancel(
                "Query input blocked: Path of Exile is not foreground.");
            return;
        }

        var panel = GameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (!panel.IsVisible || !panel.CurrencyPicker.IsVisible)
        {
            _searchQueryController.Cancel(
                "Query input blocked: the Currency Exchange picker is not visible.");
            return;
        }

        var isPickingWantedCurrency = panel.CurrencyPicker.IsPickingWantedCurrency;
        var desiredCurrency = isPickingWantedCurrency
            ? _previewStep.WantedCurrency
            : _previewStep.OfferedCurrency;
        _searchQueryController.Start(
            desiredCurrency,
            isPickingWantedCurrency,
            Settings.EnterPickerSearchQuery.Value.Key,
            _catalogue,
            out _);
    }

    private void MoveToVerifiedOption()
    {
        if (IsAnyAutomationRunning)
        {
            _inputStatus = "Mouse move blocked: automated scan is running.";
            return;
        }

        if (_pickerOpenController.IsRunning)
        {
            _cursorTweenController.Cancel(
                "Mouse move blocked: picker-open operation is running.");
            return;
        }

        _selectionController.Cancel("Option selection cancelled by a new cursor tween.");
        if (!Settings.AllowVerifiedTargetMouseMove)
        {
            _cursorTweenController.Cancel(
                "Mouse move blocked: enable Allow Verified Target Mouse Move first.");
            return;
        }

        if (_catalogue == null ||
            _searchQueryController.State != CurrencySearchQueryState.Completed ||
            _searchQueryController.TargetCurrency == null ||
            _searchQueryController.VerifiedTarget == null)
        {
            _cursorTweenController.Cancel(
                "Mouse move blocked: F9 has not verified an exact target.");
            return;
        }

        if (!GameController.Window.IsForeground())
        {
            _cursorTweenController.Cancel(
                "Mouse move blocked: Path of Exile is not foreground.");
            return;
        }

        var panel = GameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (!panel.IsVisible || !panel.CurrencyPicker.IsVisible)
        {
            _cursorTweenController.Cancel(
                "Mouse move blocked: the Currency Exchange picker is not visible.");
            return;
        }

        if (panel.CurrencyPicker.IsPickingWantedCurrency !=
            _searchQueryController.ExpectedWantedPicker)
        {
            _cursorTweenController.Cancel(
                "Mouse move blocked: the picker side changed after verification.");
            return;
        }

        if (!_pickerInspector.TryInspect(
            GameController,
            _catalogue,
            out var inspection,
            out var inspectionFailure))
        {
            _cursorTweenController.Cancel($"Mouse move blocked: {inspectionFailure}");
            return;
        }

        var targetMetadata = _searchQueryController.TargetCurrency.Metadata;
        var freshTarget = inspection!.VisibleOptions.FirstOrDefault(
            option => option.Currency.Metadata == targetMetadata);
        if (freshTarget == null)
        {
            _cursorTweenController.Cancel(
                "Mouse move blocked: the exact target is no longer visible.");
            return;
        }

        var pickerRectangle = panel.CurrencyPicker.GetClientRectCache;
        var pickerRight = pickerRectangle.X + pickerRectangle.Width;
        var pickerBottom = pickerRectangle.Y + pickerRectangle.Height;
        if (freshTarget.Center.X < pickerRectangle.X || freshTarget.Center.X > pickerRight ||
            freshTarget.Center.Y < pickerRectangle.Y || freshTarget.Center.Y > pickerBottom)
        {
            _cursorTweenController.Cancel(
                "Mouse move blocked: target center is outside the picker rectangle.");
            return;
        }

        if (!GameController.Window.IsForeground() || !panel.IsVisible ||
            !panel.CurrencyPicker.IsVisible ||
            panel.CurrencyPicker.IsPickingWantedCurrency !=
                _searchQueryController.ExpectedWantedPicker)
        {
            _cursorTweenController.Cancel(
                "Mouse move blocked: foreground or picker state changed.");
            return;
        }

        _cursorTweenController.Start(
            freshTarget.Currency,
            _searchQueryController.ExpectedWantedPicker,
            freshTarget.Center,
            Settings.CursorTweenSpeed.Value,
            out _);
    }

    private void SelectVerifiedOption()
    {
        if (IsAnyAutomationRunning)
        {
            _inputStatus = "Click blocked: automated scan is running.";
            return;
        }

        if (_pickerOpenController.IsRunning)
        {
            _selectionController.Cancel(
                "Click blocked: picker-open operation is running.");
            return;
        }

        if (!Settings.AllowVerifiedOptionClick)
        {
            _selectionController.Cancel(
                "Click blocked: enable Allow Verified Option Click first.");
            return;
        }

        if (_catalogue == null)
        {
            _selectionController.Cancel(
                "Click blocked: press F7 to build the currency catalogue first.");
            return;
        }

        if (_cursorTweenController.IsRunning)
        {
            _selectionController.Cancel(
                "Click blocked: wait for the cursor tween to complete.");
            return;
        }

        _selectionController.TryClick(
            GameController,
            _pickerInspector,
            _catalogue,
            _searchQueryController,
            _cursorTweenController,
            out _);
    }

    private void StartPickerButtonCalibration()
    {
        if (_liquidityDiscoveryController.IsRunning)
        {
            _inputStatus = "F12 calibration blocked: liquidity discovery is running.";
            return;
        }

        CancelSinglePairAutomation("Single-pair scan cancelled by calibration.");
        CancelBoundedScanAutomation("Bounded scan cancelled by calibration.");
        CancelLiquidityDiscovery("Liquidity discovery cancelled by calibration.");
        _searchQueryController.Cancel("Search query cancelled by picker-button calibration.");
        _cursorTweenController.Cancel("Cursor tween cancelled by picker-button calibration.");
        _selectionController.Cancel("Option selection cancelled by picker-button calibration.");
        _pickerOpenController.Cancel("Picker-open operation cancelled by calibration.");
        _pickerButtonCalibration.Start(GameController);
    }

    private void OpenNextPlannedPicker()
    {
        if (IsAnyAutomationRunning)
        {
            _inputStatus = "Picker open blocked: automated scan is running.";
            return;
        }

        if (!Settings.AllowCalibratedPickerOpen)
        {
            _pickerOpenController.Cancel(
                "Picker open blocked: enable Allow Calibrated Picker Open first.");
            return;
        }

        if (_previewStep == null)
        {
            _pickerOpenController.Cancel(
                "Picker open blocked: press F7 to create a scan preview first.");
            return;
        }

        if (!_pickerButtonCalibration.IsComplete)
        {
            _pickerOpenController.Cancel(
                "Picker open blocked: complete F12 picker-button calibration first.");
            return;
        }

        if (_searchQueryController.IsRunning || _cursorTweenController.IsRunning ||
            _selectionController.IsRunning)
        {
            _pickerOpenController.Cancel(
                "Picker open blocked: another input operation is running.");
            return;
        }

        var panel = GameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (!panel.IsVisible || panel.CurrencyPicker.IsVisible)
        {
            _pickerOpenController.Cancel(
                "Picker open blocked: panel must be visible with its picker closed.");
            return;
        }

        var wantedItem = panel.WantedItemType;
        var offeredItem = panel.OfferedItemType;
        bool? wantedButton = null;
        if (wantedItem == null || wantedItem.Metadata != _previewStep.WantedCurrency.Metadata)
        {
            wantedButton = true;
        }
        else if (offeredItem == null || offeredItem.Metadata != _previewStep.OfferedCurrency.Metadata)
        {
            wantedButton = false;
        }

        if (wantedButton == null)
        {
            _pickerOpenController.Cancel(
                "Picker open not needed: both currencies already match the planned pair.");
            return;
        }

        _searchQueryController.Cancel("Previous query verification cleared by picker opening.");
        _cursorTweenController.Cancel("Previous cursor verification cleared by picker opening.");
        _selectionController.Cancel("Previous option selection cleared by picker opening.");
        _pickerOpenController.Start(
            GameController,
            _pickerButtonCalibration,
            wantedButton.Value,
            Settings.CursorTweenSpeed.Value,
            out _);
    }

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

    private ExchangePairSnapshot AttachCaptureContext(
        ExchangePairSnapshot snapshot,
        ExchangeCaptureSource source)
    {
        return snapshot with
        {
            CollectorSessionId = _collectorSessionId,
            Source = source
        };
    }

    private string FormatExportStatus(RateCaptureExportResult result)
    {
        return $"Exported {result.CaptureCount} latest league/pair captures to {_exportPath}";
    }

    private bool RefreshMarketDiscovery()
    {
        if (_catalogue == null)
        {
            _marketDiscoveryDirty = true;
            return true;
        }

        var league = GetCurrentLeague();
        if (string.IsNullOrWhiteSpace(league))
        {
            _marketDiscoveryStatus = "Active-market discovery blocked: current league unavailable.";
            _marketDiscoveryDirty = true;
            _nextMarketDiscoveryRetryUtc = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
            return false;
        }

        try
        {
            _ = EnsureDiscoveryProbeRegistry();
            var durableSnapshots = _exporter.LoadSnapshots(_exportPath);
            SyncPositiveProbeOutcomes(durableSnapshots);
            var result = _marketDiscoveryExporter.Export(
                _catalogue,
                durableSnapshots,
                league,
                TimeSpan.FromMinutes(Settings.MaximumQuoteAgeMinutes.Value),
                _marketDiscoveryPath,
                _discoveryProbeOutcomes.Values,
                _discoveryOverrides.ForceSkipMetadata,
                _discoveryOverrides.ForceIncludeMetadata);
            _marketDiscoveryStatus = $"Discovery catalogue: " +
                $"{result.CatalogueCurrencyCount} currencies; " +
                $"{result.ActiveCurrencyCount} with positive evidence; " +
                $"{result.ObservedActivePairCount}/{result.EligibleProbePairCount} " +
                "currently eligible discovery pairs observed positive.";
            _marketDiscoveryDirty = false;
            _conversionGraphDirty = true;
            _nextConversionGraphRetryUtc = DateTimeOffset.UtcNow;
            _ = RefreshConversionGraph();
            if (!_activeRefreshRun || !_liquidityDiscoveryController.IsRunning)
            {
                _ = TryExportActiveRefreshPlan(GetActiveRefreshRunPlan(), out _);
            }
            return true;
        }
        catch (Exception exception)
        {
            _marketDiscoveryStatus = $"Active-market export failed: {exception.Message}";
            _marketDiscoveryDirty = true;
            _nextMarketDiscoveryRetryUtc = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
            return false;
        }
    }

    private bool RefreshConversionGraph()
    {
        if (_catalogue == null)
        {
            _conversionGraphDirty = true;
            return true;
        }

        var league = GetCurrentLeague();
        if (string.IsNullOrWhiteSpace(league))
        {
            _conversionGraphStatus = "Conversion graph blocked: current league unavailable.";
            _conversionGraphDirty = true;
            _nextConversionGraphRetryUtc = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
            return false;
        }

        try
        {
            if (!EnsureDiscoveryProbeRegistry())
            {
                throw new InvalidOperationException(
                    $"Discovery-probe registry is unavailable for graph coherence validation. " +
                    $"Market discovery status: {_marketDiscoveryStatus}");
            }

            LatestBoundedScanManifest? completedManifest = null;
            var ignoredManifestFailure = "";
            try
            {
                completedManifest = _exporter.LoadLatestCompletedBoundedScan(_exportPath);
            }
            catch (Exception exception)
            {
                ignoredManifestFailure = exception.Message;
            }

            var graphSnapshots = _exporter.LoadSnapshotsForGraph(_exportPath, league);
            var result = _conversionGraphExporter.Export(
                _catalogue,
                graphSnapshots.Snapshots,
                _discoveryProbeOutcomes.Values,
                completedManifest,
                league,
                DateTimeOffset.UtcNow,
                TimeSpan.FromMinutes(Settings.MaximumQuoteAgeMinutes.Value),
                _conversionGraphPath,
                graphSnapshots.ExcludedMalformedCount,
                _discoveryOverrides.ForceSkipMetadata);
            _conversionGraphStatus = $"Conversion graph: {result.VertexCount} vertices; " +
                $"{result.EdgeCount} coherent fresh directed edges; " +
                $"{result.ExcludedStaleCount} stale and " +
                $"{result.ExcludedIncoherentCount} incoherent captures excluded." +
                (ignoredManifestFailure.Length == 0
                    ? ""
                    : $" Invalid completed-manifest provenance was ignored: " +
                        ignoredManifestFailure);
            _conversionGraphDirty = false;
            _lastConversionGraphMaximumAgeMinutes =
                Settings.MaximumQuoteAgeMinutes.Value;
            _nextConversionGraphExpirationUtc =
                result.NextExpirationAtUtc ?? DateTimeOffset.MaxValue;
            return true;
        }
        catch (Exception exception)
        {
            _conversionGraphStatus = $"Conversion graph export failed: {exception.Message}";
            _conversionGraphDirty = true;
            _nextConversionGraphRetryUtc = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
            return false;
        }
    }

    private IReadOnlyList<CurrencyScanPlanStep> GetEligibleLiquidityDiscoverySteps(
        IReadOnlyDictionary<CurrencyPairKey, CurrencyDiscoveryProbeOutcome>? outcomes = null,
        CurrencyDiscoveryOverrides? overrides = null)
    {
        if (_scanPlan == null)
        {
            return [];
        }

        outcomes ??= _discoveryProbeOutcomes;
        overrides ??= _discoveryOverrides;
        var phaseOne = _scanPlan.InitialCollectionSteps
            .Where(step => !IsManuallySkipped(step, overrides))
            .ToArray();
        return phaseOne;
    }

    private LiquidityDiscoveryRunPlan GetLiquidityDiscoveryRunPlan()
    {
        var eligible = GetEligibleLiquidityDiscoverySteps();
        var unresolved = eligible
            .Where(step => !_discoveryProbeOutcomes.TryGetValue(step.Pair, out var outcome) ||
                outcome.Status == CurrencyDiscoveryProbeStatus.Failed)
            .ToArray();
        if (unresolved.Length > 0)
        {
            var phaseOnePairs = _scanPlan!.InitialCollectionSteps
                .Where(step => !IsManuallySkipped(step))
                .Select(step => step.Pair)
                .ToHashSet();
            var label = unresolved.Any(step => phaseOnePairs.Contains(step.Pair))
                ? "Full initial discovery"
                : "Full canonical discovery";
            return new LiquidityDiscoveryRunPlan(label, unresolved, false);
        }

        return new LiquidityDiscoveryRunPlan("Full discovery complete", [], true);
    }

    private LiquidityDiscoveryRunPlan GetActiveRefreshRunPlan()
    {
        var discoveryPlan = GetLiquidityDiscoveryRunPlan();
        if (!discoveryPlan.DiscoveryComplete)
        {
            return new LiquidityDiscoveryRunPlan(
                "Directed active refresh unavailable: F2 discovery is incomplete",
                [],
                false);
        }

        var activeRefresh = GetEligibleLiquidityDiscoverySteps()
            .Where(step => IsManuallyIncluded(step) ||
                _discoveryProbeOutcomes.TryGetValue(step.Pair, out var outcome) &&
                outcome.Status == CurrencyDiscoveryProbeStatus.Active)
            .ToArray();
        return new LiquidityDiscoveryRunPlan(
            "Directed active listing refresh",
            activeRefresh,
            true);
    }

    private bool IsManuallyIncluded(
        CurrencyScanPlanStep step,
        CurrencyDiscoveryOverrides? overrides = null)
    {
        overrides ??= _discoveryOverrides;
        return overrides.ForceIncludeMetadata.Contains(
                step.OfferedCurrency.Metadata) ||
            overrides.ForceIncludeMetadata.Contains(
                step.WantedCurrency.Metadata);
    }

    private bool IsManuallySkipped(
        CurrencyScanPlanStep step,
        CurrencyDiscoveryOverrides? overrides = null)
    {
        overrides ??= _discoveryOverrides;
        return overrides.ForceSkipMetadata.Contains(
                step.OfferedCurrency.Metadata) ||
            overrides.ForceSkipMetadata.Contains(
                step.WantedCurrency.Metadata);
    }

    private readonly record struct LiquidityDiscoveryRunPlan(
        string Label,
        IReadOnlyList<CurrencyScanPlanStep> Steps,
        bool DiscoveryComplete);

    private void SyncPositiveProbeOutcomes(
        IReadOnlyCollection<ExchangePairSnapshot> snapshots)
    {
        if (_catalogue == null || _scanPlan == null ||
            string.IsNullOrWhiteSpace(_discoveryProbeLeague))
        {
            return;
        }

        var eligiblePairs = GetEligibleLiquidityDiscoverySteps()
            .Select(step => step.Pair)
            .ToHashSet();
        var updated = new Dictionary<CurrencyPairKey, CurrencyDiscoveryProbeOutcome>(
            _discoveryProbeOutcomes);
        var changed = false;
        foreach (var snapshot in snapshots)
        {
            if (!string.Equals(
                    snapshot.League,
                    _discoveryProbeLeague,
                    StringComparison.Ordinal) ||
                snapshot.TopImmediateStock == null && snapshot.TopCompetingStock == null ||
                !eligiblePairs.Contains(snapshot.Pair) ||
                updated.TryGetValue(snapshot.Pair, out var existing) &&
                    existing.ObservedAtUtc >= snapshot.CapturedAtUtc)
            {
                continue;
            }

            updated[snapshot.Pair] = new CurrencyDiscoveryProbeOutcome(
                _discoveryProbeLeague,
                snapshot.OfferedCurrency,
                snapshot.WantedCurrency,
                CurrencyDiscoveryProbeStatus.Active,
                snapshot.CapturedAtUtc,
                snapshot.ScanId ?? snapshot.CaptureId,
                snapshot.ScanSequence ?? 1,
                snapshot.CaptureId,
                null);
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        _discoveryProbeStore.Save(
            updated.Values.ToArray(),
            _discoveryProbeLeague,
            _catalogue.Items.Count,
            GetEligibleLiquidityDiscoverySteps(updated).Count,
            _discoveryProbePath);
        _discoveryProbeOutcomes = updated;
    }

    private bool EnsureDiscoveryProbeRegistry()
    {
        if (_catalogue == null)
        {
            return false;
        }

        var league = GetCurrentLeague();
        if (string.IsNullOrWhiteSpace(league))
        {
            return false;
        }

        if (!EnsureDiscoveryOverrides())
        {
            return false;
        }

        if (string.Equals(_discoveryProbeLeague, league, StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            _discoveryProbePath = GetDiscoveryProbePath(league);
            if (_scanPlan == null &&
                !CurrencyScanPlan.TryCreate(_catalogue, out _scanPlan, out _))
            {
                return false;
            }

            var loaded = _discoveryProbeStore.Load(_discoveryProbePath, league);
            var outcomes = loaded.ToDictionary(outcome => outcome.Pair);
            var eligiblePairs = GetEligibleLiquidityDiscoverySteps(outcomes)
                .Select(step => step.Pair)
                .ToHashSet();
            foreach (var snapshot in _exporter
                .LoadSnapshotsForGraph(_exportPath, league)
                .Snapshots)
            {
                if (!string.Equals(snapshot.League, league, StringComparison.Ordinal) ||
                    snapshot.MarketRate == null ||
                    !eligiblePairs.Contains(snapshot.Pair) ||
                    outcomes.ContainsKey(snapshot.Pair))
                {
                    continue;
                }

                outcomes[snapshot.Pair] = new CurrencyDiscoveryProbeOutcome(
                    league,
                    snapshot.OfferedCurrency,
                    snapshot.WantedCurrency,
                    CurrencyDiscoveryProbeStatus.Active,
                    snapshot.CapturedAtUtc,
                    snapshot.ScanId ?? snapshot.CaptureId,
                    snapshot.ScanSequence ?? 1,
                    snapshot.CaptureId,
                    null);
            }

            _discoveryProbeStore.Save(
                outcomes.Values.ToArray(),
                league,
                _catalogue.Items.Count,
                GetEligibleLiquidityDiscoverySteps(outcomes).Count,
                _discoveryProbePath);
            _discoveryProbeOutcomes = outcomes;
            _discoveryProbeLeague = league;
            return true;
        }
        catch (Exception exception)
        {
            _marketDiscoveryStatus = $"Discovery-probe registry failed: {exception.Message}";
            return false;
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        return string.Concat(value.Select(character =>
            invalidCharacters.Contains(character) ? '_' : character));
    }

    private string GetDiscoveryProbePath(string league)
    {
        return Path.Combine(
            ConfigDirectory,
            $"FaustusController_discovery-probes-{SanitizeFileName(league)}.json");
    }

    private string GetDiscoveryOverridePath(string league)
    {
        return Path.Combine(
            ConfigDirectory,
            $"FaustusController_discovery-overrides-{SanitizeFileName(league)}.json");
    }

    private string GetActiveRefreshPlanPath(string league)
    {
        return Path.Combine(
            ConfigDirectory,
            $"FaustusController_refresh-plan-{SanitizeFileName(league)}.json");
    }

    private bool TryExportActiveRefreshPlan(
        LiquidityDiscoveryRunPlan plan,
        out string failureReason)
    {
        if (_catalogue == null)
        {
            failureReason = "the live catalogue is unavailable.";
            _activeRefreshStatus = $"Active refresh plan failed: {failureReason}";
            return false;
        }

        var league = GetCurrentLeague();
        if (string.IsNullOrWhiteSpace(league))
        {
            failureReason = "the current league is unavailable.";
            _activeRefreshStatus = $"Active refresh plan failed: {failureReason}";
            return false;
        }

        try
        {
            _activeRefreshPlanPath = GetActiveRefreshPlanPath(league);
            var result = _activeRefreshPlanExporter.Export(
                _catalogue,
                league,
                plan.DiscoveryComplete,
                plan.DiscoveryComplete ? plan.Steps : [],
                _discoveryProbeOutcomes,
                _discoveryOverrides,
                _activeRefreshPlanPath);
            _activeRefreshStatus = result.Ready
                ? $"Insert refresh plan: {result.PairCount} directed pairs " +
                    $"({result.CanonicalActivePairCount} canonical active, " +
                    $"{result.ForceIncludedPairCount} forced)."
                    : "Insert refresh plan is not ready: complete F2 discovery first.";
            _activeRefreshPlanDirty = false;
            if (!result.Ready)
            {
                failureReason = "F2 discovery still has unresolved directed pairs.";
                return false;
            }

            failureReason = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            failureReason = $"refresh-plan export failed: {exception.Message}";
            _activeRefreshStatus = $"Active refresh plan failed: {exception.Message}";
            _activeRefreshPlanDirty = true;
            _nextActiveRefreshPlanRetryUtc = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
            return false;
        }
    }

    private bool EnsureDiscoveryOverrides(bool forceReload = false)
    {
        if (_catalogue == null)
        {
            return false;
        }

        var league = GetCurrentLeague();
        if (string.IsNullOrWhiteSpace(league))
        {
            return false;
        }

        if (!forceReload && string.Equals(
            _discoveryOverrideLeague,
            league,
            StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            var path = GetDiscoveryOverridePath(league);
            var loaded = _discoveryOverrideStore.LoadOrCreate(path, league, _catalogue);
            var changed = !string.Equals(
                    _discoveryOverrideLeague,
                    league,
                    StringComparison.Ordinal) ||
                !new HashSet<string>(
                    _discoveryOverrides.ForceIncludeMetadata,
                    StringComparer.Ordinal)
                    .SetEquals(loaded.ForceIncludeMetadata) ||
                !new HashSet<string>(
                    _discoveryOverrides.ForceSkipMetadata,
                    StringComparer.Ordinal)
                    .SetEquals(loaded.ForceSkipMetadata);
            if (changed)
            {
                if (_scanPlan != null && string.Equals(
                    _discoveryProbeLeague,
                    league,
                    StringComparison.Ordinal))
                {
                    _discoveryProbeStore.Save(
                        _discoveryProbeOutcomes.Values.ToArray(),
                        league,
                        _catalogue.Items.Count,
                        GetEligibleLiquidityDiscoverySteps(
                            _discoveryProbeOutcomes,
                            loaded).Count,
                        _discoveryProbePath);
                }

                _marketDiscoveryDirty = true;
                _nextMarketDiscoveryRetryUtc = DateTimeOffset.UtcNow;
                _conversionGraphDirty = true;
                _nextConversionGraphRetryUtc = DateTimeOffset.UtcNow;
            }

            _discoveryOverridePath = path;
            _discoveryOverrideLeague = league;
            _discoveryOverrides = loaded;

            return true;
        }
        catch (Exception exception)
        {
            _marketDiscoveryStatus = $"Discovery overrides failed: {exception.Message}";
            return false;
        }
    }

    private bool TryPersistDiscoveryProbe(PendingDiscoveryProbe pendingProbe)
    {
        if (_catalogue == null || _scanPlan == null)
        {
            return false;
        }

        var pendingIsLiveLeague = string.Equals(
            pendingProbe.League,
            GetCurrentLeague(),
            StringComparison.Ordinal);
        if (pendingIsLiveLeague && !EnsureDiscoveryProbeRegistry())
        {
            return false;
        }

        if (pendingProbe.Status == CurrencyDiscoveryProbeStatus.Active)
        {
            if (pendingProbe.Snapshot == null ||
                !StoreAndExportAutomatedSnapshot(
                    pendingProbe.Snapshot,
                    ExchangeCaptureSource.LiquidityDiscoveryAutomation,
                    "Liquidity discovery"))
            {
                return false;
            }
        }

        var outcome = new CurrencyDiscoveryProbeOutcome(
            pendingProbe.League,
            pendingProbe.Step.OfferedCurrency,
            pendingProbe.Step.WantedCurrency,
            pendingProbe.Status,
            pendingProbe.ObservedAtUtc,
            pendingProbe.RunId,
            pendingProbe.RunSequence,
            pendingProbe.Snapshot?.CaptureId,
            pendingProbe.FailureReason);
        var isCurrentRegistry = string.Equals(
            pendingProbe.League,
            _discoveryProbeLeague,
            StringComparison.Ordinal);
        var probePath = GetDiscoveryProbePath(pendingProbe.League);
        var sourceOutcomes = isCurrentRegistry
            ? _discoveryProbeOutcomes
            : _discoveryProbeStore.Load(probePath, pendingProbe.League)
                .ToDictionary(item => item.Pair);
        if (_activeRefreshRun &&
            pendingProbe.Status == CurrencyDiscoveryProbeStatus.Failed &&
            sourceOutcomes.TryGetValue(outcome.Pair, out var priorOutcome) &&
            priorOutcome.Status != CurrencyDiscoveryProbeStatus.Failed)
        {
            _exportStatus = "Active refresh failed without replacing the prior terminal " +
                $"{priorOutcome.Status} outcome for " +
                $"{pendingProbe.Step.OfferedCurrency.Name} -> " +
                $"{pendingProbe.Step.WantedCurrency.Name}.";
            return true;
        }

        var updated = new Dictionary<CurrencyPairKey, CurrencyDiscoveryProbeOutcome>(
            sourceOutcomes)
        {
            [outcome.Pair] = outcome
        };

        try
        {
            _discoveryProbeStore.Save(
                updated.Values.ToArray(),
                pendingProbe.League,
                _catalogue.Items.Count,
                GetEligibleLiquidityDiscoverySteps(updated).Count,
                probePath);
            if (!isCurrentRegistry)
            {
                return true;
            }

            _discoveryProbeOutcomes = updated;
            return pendingProbe.Status == CurrencyDiscoveryProbeStatus.Failed ||
                RefreshMarketDiscovery();
        }
        catch (Exception exception)
        {
            _exportStatus = $"Discovery-probe persistence failed: {exception.Message}";
            return false;
        }
    }

    private void EnsureMarketDiscoveryCatalogue()
    {
        if (_catalogue != null || DateTimeOffset.UtcNow < _nextDiscoveryCatalogueAttemptUtc)
        {
            return;
        }

        _nextDiscoveryCatalogueAttemptUtc = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        if (!_catalogueBuilder.TryBuild(
            GameController,
            out var catalogue,
            out var failureReason))
        {
            _marketDiscoveryStatus = $"Active-market discovery waiting: {failureReason}";
            return;
        }

        _catalogue = catalogue;
        _marketDiscoveryDirty = true;
        _conversionGraphDirty = true;
        _ = EnsureDiscoveryProbeRegistry();
        _ = RefreshMarketDiscovery();
    }

    private void RunRouteAnalysis()
    {
        if (IsAnyAutomationRunning)
        {
            _routeAnalysisStatus = "Route analysis blocked: automated scan is running.";
            return;
        }

        if (_catalogue == null)
        {
            _routeAnalysisStatus = "Route analysis blocked: the live catalogue is unavailable.";
            return;
        }

        var league = GetCurrentLeague();
        if (string.IsNullOrWhiteSpace(league))
        {
            _routeAnalysisStatus = "Route analysis blocked: the current league is unavailable.";
            return;
        }

        try
        {
            if (!RefreshConversionGraph())
            {
                throw new InvalidOperationException(_conversionGraphStatus);
            }

            var sanitizedLeague = SanitizeFileName(league);
            _routeRequestPath = Path.Combine(
                ConfigDirectory,
                $"FaustusController_route-request-{sanitizedLeague}.json");
            _routeAnalysisPath = Path.Combine(
                ConfigDirectory,
                $"FaustusController_route-analysis-{sanitizedLeague}.json");
            var result = _routeAnalyzer.Analyze(
                _catalogue,
                league,
                _conversionGraphPath,
                _routeRequestPath,
                _routeAnalysisPath,
                DateTimeOffset.UtcNow);
            _routeAnalysisStatus = result.RouteFound
                ? $"Route analysis: best output {result.BestTargetUnits}; " +
                    $"{result.CandidateRouteCount} candidates, {result.FreshEdgeCount} fresh edges" +
                    (result.SearchTruncated ? "; SEARCH TRUNCATED." : ".") +
                    FormatRouteConstraints(result)
                : $"Route analysis found no executable whole-unit route across " +
                    $"{result.FreshEdgeCount} fresh edges; {result.ExpiredEdgeCount} expired." +
                    FormatRouteConstraints(result);

            try
            {
                _lastRouteAnalysis = JsonConvert.DeserializeObject<CurrencyRouteAnalysisFile>(
                    File.ReadAllText(_routeAnalysisPath));
            }
            catch
            {
                _lastRouteAnalysis = null;
            }
        }
        catch (Exception exception)
        {
            _routeAnalysisStatus = $"Route analysis failed: {exception.Message}";
            _lastRouteAnalysis = null;
        }
    }

    private static string FormatRouteConstraints(CurrencyRouteAnalysisResult result)
    {
        var constraints = new List<string>();
        if (result.UsesInventoryBalances)
        {
            constraints.Add("inventory balances");
        }

        if (result.UsesLiquidityLimits)
        {
            constraints.Add("liquidity limits");
        }

        if (result.UsesGoldCosts)
        {
            constraints.Add("gold costs");
        }

        return constraints.Count > 0
            ? $" [{string.Join(", ", constraints)}]"
            : "";
    }

    private string GetCurrentLeague()
    {
        try
        {
            return GameController.Game.IngameState.ServerData.League ?? "";
        }
        catch
        {
            return "";
        }
    }

    private string FormatScanStep(CurrencyScanPlanStep step)
    {
        var collectionIndex = _scanPlan!.InitialCollectionSteps
            .Select((candidate, index) => (candidate, index))
            .First(entry => entry.candidate.Pair == step.Pair)
            .index;
        return $"Initial collection {collectionIndex + 1}/" +
            $"{_scanPlan.InitialCollectionSteps.Count} ({_catalogue!.Items.Count} currencies): " +
            $"{step.OfferedCurrency.Name} -> {step.WantedCurrency.Name}.";
    }

    private static string FormatRatio(RationalExchangeRate? rate)
    {
        return rate == null ? "unavailable" : $"{rate.GetUnits}:{rate.GiveUnits}";
    }

    private void RenderRouteAnalysis(int startY)
    {
        var analysis = _lastRouteAnalysis;
        if (analysis == null || analysis.BestRoute == null)
        {
            return;
        }

        var y = startY;
        var x = 100;
        var best = analysis.BestRoute;

        Graphics.DrawText(
            $"=== ROUTE ANALYSIS DRY RUN (Rank {best.Rank}) ===",
            new Vector2(x, y),
            SharpDX.Color.White);
        y += 20;

        Graphics.DrawText(
            $"{analysis.Request.StartAmount} {best.Hops.FirstOrDefault()?.OfferedCurrency.Name} " +
            $"-> {best.TargetUnits} {best.TargetCurrency.Name} " +
            $"in {best.HopCount} hops | gold: {best.TotalGoldCost} | " +
            $"stranded: {best.StrandedRemainderCurrencyCount} currency",
            new Vector2(x, y),
            SharpDX.Color.Cyan);
        y += 20;

        foreach (var hop in best.Hops)
        {
            Graphics.DrawText(
                $"  Hop {hop.Sequence}: {hop.OfferedCurrency.Name} -> {hop.WantedCurrency.Name}",
                new Vector2(x, y),
                SharpDX.Color.White);
            y += 20;

            Graphics.DrawText(
                $"    avail: {hop.AvailableBefore} | lots: {hop.Lots} | " +
                $"rate: {hop.GetUnitsPerLot}:{hop.GiveUnitsPerLot} | " +
                $"spent: {hop.Spent} | recv: {hop.Received} | rem: {hop.Remainder}",
                new Vector2(x, y),
                SharpDX.Color.White);
            y += 20;

            var liquidityLabel = analysis.UsesLiquidityLimits
                ? $"fillable: {hop.FillableLots} | capped: {hop.LotsCappedByLiquidity.ToString().ToLowerInvariant()}"
                : "liquidity: disabled";
            Graphics.DrawText(
                $"    {hop.BookSide} | {hop.Coherence} | gold: {hop.GoldCost} | {liquidityLabel}",
                new Vector2(x, y),
                SharpDX.Color.White);
            y += 20;
        }

        foreach (var remainder in best.Remainders.Where(r => r.Units > 0))
        {
            Graphics.DrawText(
                $"  Remainder: {remainder.Units} {remainder.Currency.Name}",
                new Vector2(x, y),
                SharpDX.Color.Yellow);
            y += 20;
        }

        var rejectionParts = new List<string>();
        if (analysis.RejectedCycleCount > 0)
        {
            rejectionParts.Add($"cycles {analysis.RejectedCycleCount}");
        }

        if (analysis.RejectedZeroLotCount > 0)
        {
            rejectionParts.Add($"zero-lot {analysis.RejectedZeroLotCount}");
        }

        if (analysis.RejectedOverflowCount > 0)
        {
            rejectionParts.Add($"overflow {analysis.RejectedOverflowCount}");
        }

        if (analysis.RejectedLiquidityLimitCount > 0)
        {
            rejectionParts.Add($"liquidity {analysis.RejectedLiquidityLimitCount}");
        }

        if (analysis.RejectedGoldBudgetCount > 0)
        {
            rejectionParts.Add($"gold {analysis.RejectedGoldBudgetCount}");
        }

        var rejectionLabel = rejectionParts.Count > 0
            ? string.Join(", ", rejectionParts)
            : "none";
        Graphics.DrawText(
            $"Candidates: {analysis.CandidateRouteCount} | expanded: {analysis.ExpandedStateCount} | " +
            $"rejected: {rejectionLabel}",
            new Vector2(x, y),
            SharpDX.Color.Orange);
        y += 20;

        var constraintParts = new List<string>();
        if (analysis.UsesInventoryBalances)
        {
            constraintParts.Add("inventory balances");
        }

        if (analysis.UsesLiquidityLimits)
        {
            constraintParts.Add("liquidity limits");
        }

        if (analysis.UsesGoldCosts)
        {
            constraintParts.Add("gold costs");
        }

        var constraintLabel = constraintParts.Count > 0
            ? string.Join(", ", constraintParts)
            : "none";
        Graphics.DrawText(
            $"Constraints: {constraintLabel} | truncated: {analysis.SearchTruncated.ToString().ToLowerInvariant()} | " +
            $"routes: {analysis.Routes.Count}",
            new Vector2(x, y),
            SharpDX.Color.Orange);
    }

    public override void Render()
    {
        var currentLeague = GetCurrentLeague();
        var freshQuoteCount = _rateBook.CountFreshMarketQuotes(
            currentLeague,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(Settings.MaximumQuoteAgeMinutes.Value));
        Graphics.DrawText(_captureStatus, new Vector2(100, 100), SharpDX.Color.White);
        Graphics.DrawText(
            $"Latest captures: {_rateBook.Count}; " +
            $"{freshQuoteCount} fresh latest market quotes in " +
            $"{(string.IsNullOrWhiteSpace(currentLeague) ? "unknown league" : currentLeague)}",
            new Vector2(100, 120),
            SharpDX.Color.White);
        Graphics.DrawText(_exportStatus, new Vector2(100, 140), SharpDX.Color.White);
        Graphics.DrawText(_scanStatus, new Vector2(100, 160), SharpDX.Color.Yellow);
        Graphics.DrawText(_inputStatus, new Vector2(100, 180), SharpDX.Color.Cyan);
        Graphics.DrawText(
            _searchQueryController.Status,
            new Vector2(100, 200),
            SharpDX.Color.Cyan);
        Graphics.DrawText(
            _cursorTweenController.Status,
            new Vector2(100, 220),
            SharpDX.Color.Orange);
        Graphics.DrawText(
            _selectionController.Status,
            new Vector2(100, 240),
            SharpDX.Color.Orange);
        Graphics.DrawText(
            _pickerButtonCalibration.Status,
            new Vector2(100, 260),
            SharpDX.Color.Magenta);
        Graphics.DrawText(
            _pickerOpenController.Status,
            new Vector2(100, 280),
            SharpDX.Color.Magenta);
        Graphics.DrawText(
            _singlePairScanController.Status,
            new Vector2(100, 300),
            SharpDX.Color.LimeGreen);
        Graphics.DrawText(
            _boundedScanController.Status,
            new Vector2(100, 320),
            SharpDX.Color.LimeGreen);
        Graphics.DrawText(
            _marketDiscoveryStatus,
            new Vector2(100, 340),
            SharpDX.Color.Yellow);
        Graphics.DrawText(
            _liquidityDiscoveryController.Status,
            new Vector2(100, 360),
            SharpDX.Color.LimeGreen);
        Graphics.DrawText(
            _conversionGraphStatus,
            new Vector2(100, 380),
            SharpDX.Color.Cyan);
        Graphics.DrawText(
            _activeRefreshStatus,
            new Vector2(100, 400),
            SharpDX.Color.Cyan);
        Graphics.DrawText(
            _routeAnalysisStatus,
            new Vector2(100, 420),
            SharpDX.Color.Cyan);

        RenderRouteAnalysis(440);

        if (_previewTarget != null)
        {
            Graphics.DrawText(
                $"DRY RUN TARGET: {_previewTarget.Currency.Name}",
                _previewTarget.Center + new Vector2(6, -10),
                SharpDX.Color.Yellow);
        }

        var panel = GameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (panel.IsVisible)
        {
            var panelRectangle = panel.GetClientRectCache;
            if (_pickerButtonCalibration.TryResolve(
                panelRectangle,
                wantedButton: true,
                out var wantedButton))
            {
                Graphics.DrawText(
                    "CALIBRATED I WANT",
                    wantedButton + new Vector2(6, -10),
                    SharpDX.Color.Magenta);
            }

            if (_pickerButtonCalibration.TryResolve(
                panelRectangle,
                wantedButton: false,
                out var offeredButton))
            {
                Graphics.DrawText(
                    "CALIBRATED I HAVE",
                    offeredButton + new Vector2(6, -10),
                    SharpDX.Color.Magenta);
            }
        }
    }

}
