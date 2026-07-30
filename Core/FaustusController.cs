using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using Newtonsoft.Json;
using System.Windows.Forms;
using Vector2 = System.Numerics.Vector2;

namespace FaustusController;

public sealed partial class FaustusController : BaseSettingsPlugin<FaustusControllerSettings>
{
    private readonly CurrencyExchangeRateCollector _collector = new();
    private readonly CurrencyCatalogueBuilder _catalogueBuilder = new();
    private readonly CurrencyPickerInspector _pickerInspector = new();
    private readonly CurrencySearchQueryController _searchQueryController = new();
    private readonly CursorTweenController _cursorTweenController = new();
    private readonly VerifiedOptionSelectionController _selectionController = new();
    private readonly CurrencyAmountInputController _amountInputController = new();
    private readonly OrderStagingController _orderStagingController = new();
    private readonly OrderPlacementController _orderPlacementController = new();
    private readonly SingleHopExecutionController _singleHopExecutionController = new();
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
    private string _captureStatus = "";
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
    private string _discoveryCategorySignature = "";
    private TradableCategoryResolver _tradableCategories = TradableCategoryResolver.Empty;
    private string _tradablesStatus = "Tradables category list not loaded yet.";
    private bool _activeRefreshRun;
    private string _exportStatus = "";
    private string _marketDiscoveryStatus = "Active-market discovery is waiting for the live catalogue.";
    private string _conversionGraphStatus = "Conversion graph is waiting for the live catalogue.";
    private string _activeRefreshStatus = "Active refresh plan is waiting for discovery.";
    private string _routeAnalysisStatus = "Press Home to create/run exact route analysis.";
    private CurrencyRouteAnalysisFile? _lastRouteAnalysis;
    private int _routeDisplayIndex;
    private string _inputStatus = "";

    public override bool Initialise()
    {
        Settings.CalibratePickerButtons.IgnoreFocusedInput = true;
        Settings.RunLiquidityDiscoveryAutomation.IgnoreFocusedInput = true;
        Settings.RunActiveRefreshAutomation.IgnoreFocusedInput = true;
        Settings.RunRouteAnalysis.IgnoreFocusedInput = true;
        Settings.CycleRouteUp.IgnoreFocusedInput = true;
        Settings.CycleRouteDown.IgnoreFocusedInput = true;
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

        try
        {
            var tradablesPath = FindTradablesPath();
            if (tradablesPath.Length == 0)
            {
                _tradablesStatus =
                    "Tradables list NOT found (tradables.json / tradables.txt); every " +
                    "currency is 'Other'. Place tradables.json in the plugin folder.";
            }
            else
            {
                _tradableCategories = TradableCategoryResolver.Load(tradablesPath);
                _tradablesStatus =
                    $"Tradables loaded ({_tradableCategories.NamedEntryCount}): " +
                    $"Div {_tradableCategories.CountFor(TradableCategory.DivinationCards)}, " +
                    $"Cur {_tradableCategories.CountFor(TradableCategory.Currency)}, " +
                    $"Deli {_tradableCategories.CountFor(TradableCategory.DeliriumOrbs)}, " +
                    $"Scar {_tradableCategories.CountFor(TradableCategory.Scarabs)}, " +
                    $"Foss {_tradableCategories.CountFor(TradableCategory.Fossils)}, " +
                    $"Ess {_tradableCategories.CountFor(TradableCategory.Essences)} " +
                    $"from {Path.GetFileName(tradablesPath)}.";
            }
        }
        catch (Exception exception)
        {
            // Everything falls to 'Other' when the list cannot be parsed.
            _tradableCategories = TradableCategoryResolver.Empty;
            _tradablesStatus = $"Tradables load FAILED: {exception.Message}";
        }

        return true;
    }

    // tradables.json (preferred) or tradables.txt, searched next to the plugin
    // and up its directory tree, then next to the config directory. The plugin's
    // runtime folder is not always where the source lives, so several roots are
    // probed rather than assuming DirectoryFullName.
    private string FindTradablesPath()
    {
        var roots = new List<string>();
        void AddRootChain(string? start)
        {
            var directory = string.IsNullOrEmpty(start) ? null : new DirectoryInfo(start);
            for (var depth = 0; directory != null && depth < 6; depth++)
            {
                roots.Add(directory.FullName);
                directory = directory.Parent;
            }
        }

        AddRootChain(DirectoryFullName);
        AddRootChain(ConfigDirectory);
        AddRootChain(AppContext.BaseDirectory);

        foreach (var fileName in new[] { "tradables.json", "tradables.txt" })
        {
            foreach (var root in roots)
            {
                var candidate = Path.Combine(root, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return "";
    }

    public override void AreaChange(AreaInstance area)
    {
        _cursorTweenController.Cancel("Cursor tween cancelled after area change.");
        _selectionController.Cancel("Option selection verification cancelled after area change.");
        _pickerButtonCalibration.Cancel("Picker-button calibration cancelled after area change.");
        _pickerOpenController.Cancel("Picker-open operation cancelled after area change.");
        _singlePairScanController.Cancel("Single-pair scan cancelled after area change.");
        _boundedScanController.Cancel("Bounded scan cancelled after area change.");
        _liquidityDiscoveryController.Cancel("Liquidity discovery cancelled after area change.");
        _searchQueryController.Cancel("Search query cancelled after area change.");
        _amountInputController.Cancel("Order amount input cancelled after area change.");
        _orderStagingController.Cancel("Order staging cancelled after area change.");
        _orderPlacementController.Cancel("Order placement cancelled after area change.");
        _singleHopExecutionController.Cancel(
            "Single-hop execution cancelled after area change.");
        _marketDiscoveryDirty = true;
        _nextMarketDiscoveryRetryUtc = DateTimeOffset.UtcNow;
        _conversionGraphDirty = true;
        _nextConversionGraphRetryUtc = DateTimeOffset.UtcNow;
        _captureStatus = "Area changed; captured rate snapshots were retained.";
    }

    public override Job Tick()
    {
        EnsureMarketDiscoveryCatalogue();
        // One setting, every scan controller: the standalone one plus each that
        // owns its own inner SinglePairScanController (discovery, bounded, staging).
        var stableRateSampleTarget = Settings.StableRateSampleCount.Value;
        _singlePairScanController.StableRateSampleTarget = stableRateSampleTarget;
        _liquidityDiscoveryController.StableRateSampleTarget = stableRateSampleTarget;
        _boundedScanController.StableRateSampleTarget = stableRateSampleTarget;
        _orderStagingController.StableRateSampleTarget = stableRateSampleTarget;

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

        if (Settings.CycleRouteUp.PressedOnce() || Settings.CycleRouteDown.PressedOnce())
        {
            CycleRouteDisplay(Settings.CycleRouteUp.PressedOnce());
        }

        if (Settings.SyncInventoryFromPicker.PressedOnce())
        {
            SyncInventoryFromPicker();
        }

        if (Settings.ExportPlacedOrders.PressedOnce())
        {
            ExportPlacedOrders();
        }

        if (Settings.CalibrateGoldCost.PressedOnce())
        {
            CalibrateGoldCostFromOrders();
        }

        if (Settings.CalibratePickerButtons.PressedOnce())
        {
            StartPickerButtonCalibration();
        }

        if (Settings.TypeOfferedAmountKey.PressedOnce())
        {
            StartOrderAmountInput(wantedInput: false);
        }

        if (Settings.TypeWantedAmountKey.PressedOnce())
        {
            StartOrderAmountInput(wantedInput: true);
        }

        if (Settings.StageOrderDryRunKey.PressedOnce())
        {
            StartOrderStagingDryRun();
        }

        if (Settings.DumpSdkReadsKey.PressedOnce())
        {
            DumpSdkReads();
        }

        if (Settings.CalibratePlaceOrderButtonKey.PressedOnce())
        {
            _pickerButtonCalibration.CapturePlaceOrderButton(GameController);
        }

        if (Settings.PlaceOrderKey.PressedOnce())
        {
            StartOrderPlacement();
        }

        if (Settings.ExecuteHopKey.PressedOnce())
        {
            StartSingleHopExecution();
        }

        if (_orderStagingController.IsRunning && !AreOrderStagingPermissionsEnabled())
        {
            _orderStagingController.Cancel(
                "Order staging cancelled: one or more required permission toggles were disabled.");
        }

        if (_orderPlacementController.IsRunning && !AreOrderPlacementPermissionsEnabled())
        {
            CancelOrderPlacement(
                "Order placement cancelled: one or more required permission toggles were disabled.");
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
        if (_amountInputController.IsRunning && !Settings.AllowOrderAmountInput)
        {
            _amountInputController.Cancel(
                "Amount input cancelled: Allow Order Amount Input was disabled.");
        }
        else
        {
            _amountInputController.Tick(GameController);
        }

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

            _orderStagingController.Tick(
                GameController,
                _catalogue,
                _pickerButtonCalibration,
                _pickerOpenController,
                _pickerInspector,
                _searchQueryController,
                _cursorTweenController,
                _selectionController,
                _collector,
                _amountInputController,
                Settings.CursorTweenSpeed.Value);

            var stagedSnapshot = _orderStagingController.TakeStagedSnapshot();
            if (stagedSnapshot != null)
            {
                _ = StoreAndExportAutomatedSnapshot(
                    stagedSnapshot,
                    ExchangeCaptureSource.SinglePairAutomation,
                    "Order staging");
            }
        }

        _orderPlacementController.Tick(
            GameController,
            _orderStagingController,
            _pickerButtonCalibration,
            Settings.CursorTweenSpeed.Value);
        if (_orderPlacementController.TakeCompletedNotification())
        {
            ExportPlacedOrders();
        }

        // Ticked after placement so a Completed order and its outcome are visible
        // this frame; drains the finished execution's audit for persistence.
        TickSingleHopExecution();

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
}
