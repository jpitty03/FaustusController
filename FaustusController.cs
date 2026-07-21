using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
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
    private readonly ExchangeRateBook _rateBook = new();
    private readonly RateCaptureJsonExporter _exporter = new();
    private CurrencyCatalogue? _catalogue;
    private CurrencyScanPlan? _scanPlan;
    private CurrencyScanPlanStep? _previewStep;
    private CurrencyPickerOptionTarget? _previewTarget;
    private string _captureStatus = "Use the capture hotkey with an exchange pair selected.";
    private string _exportPath = "";
    private string _pickerButtonCalibrationPath = "";
    private string _exportStatus = "";
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
        _exportPath = Path.Combine(ConfigDirectory, "FaustusController_rate-captures.json");
        _pickerButtonCalibrationPath = Path.Combine(
            ConfigDirectory,
            "FaustusController_picker-buttons.json");
        if (File.Exists(_exportPath))
        {
            try
            {
                var exportedCount = _exporter.Export([], _exportPath);
                _exportStatus = $"Validated {exportedCount} exported pairs at {_exportPath}";
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
        _searchQueryController.Cancel("Search query cancelled after area change.");
        _captureStatus = "Area changed; captured rate snapshots were retained.";
        _scanStatus = "Dry-run preview cleared after area change.";
    }

    public override Job Tick()
    {
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
                    _rateBook.Store(snapshot!);
                    var immediateStock = snapshot!.TopImmediateStock;
                    var rawImmediate = immediateStock == null
                        ? ""
                        : $" (raw wanted {immediateStock.RawGet}:{immediateStock.RawGive})";
                    var competingStock = snapshot.TopCompetingStock;
                    var rawCompeting = competingStock == null
                        ? ""
                        : $" (raw opposite {competingStock.RawGet}:{competingStock.RawGive})";
                    _captureStatus = $"Captured {snapshot!.OfferedCurrency.Name} -> " +
                        $"{snapshot.WantedCurrency.Name}. Market: " +
                        $"{FormatRatio(snapshot.MarketRate)}; immediate: " +
                        $"{FormatRatio(snapshot.TopImmediateRate)}{rawImmediate}; " +
                        $"competing: {FormatRatio(snapshot.TopCompetingRate)}{rawCompeting}.";

                    try
                    {
                        var exportedCount = _exporter.Export(
                            _rateBook.LatestSnapshots,
                            _exportPath);
                        _exportStatus = $"Exported {exportedCount} pairs to {_exportPath}";
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
            StoreAndExportAutomatedSnapshot(automatedSnapshot, "Single-pair");
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
            if (StoreAndExportAutomatedSnapshot(boundedSnapshot, "Bounded scan"))
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

        return null!;
    }

    private void StartDryRunPreview()
    {
        CancelSinglePairAutomation("Single-pair scan cancelled by a new preview.");
        CancelBoundedScanAutomation("Bounded scan cancelled by a new preview.");
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

        _previewStep = _scanPlan!.InitialCollectionSteps.FirstOrDefault();
        if (_previewStep == null)
        {
            _scanStatus = "The generated scan plan is empty.";
            return;
        }

        _scanStatus = FormatScanStep(_previewStep);
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
        CancelSinglePairAutomation("Single-pair scan cancelled by calibration.");
        CancelBoundedScanAutomation("Bounded scan cancelled by calibration.");
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
        if (_boundedScanController.IsRunning)
        {
            _singlePairScanController.Cancel(
                "Single-pair scan blocked: bounded scan is running.");
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
            _boundedScanController.Cancel(
                "Bounded scan blocked: enable Allow Bounded Scan Automation first.");
            return;
        }

        if (!AreBoundedScanPermissionsEnabled())
        {
            _boundedScanController.Cancel(
                "Bounded scan blocked: enable all picker, query, movement, and click permissions.");
            return;
        }

        if (_singlePairScanController.IsRunning)
        {
            _boundedScanController.Cancel(
                "Bounded scan blocked: single-pair scan is running.");
            return;
        }

        if (_previewStep == null || _catalogue == null || _scanPlan == null)
        {
            _boundedScanController.Cancel(
                "Bounded scan blocked: press F7 to create an initial-scope preview first.");
            return;
        }

        if (!_pickerButtonCalibration.IsComplete)
        {
            _boundedScanController.Cancel(
                "Bounded scan blocked: complete F12 calibration first.");
            return;
        }

        if (_pickerOpenController.IsRunning || _searchQueryController.IsRunning ||
            _cursorTweenController.IsRunning || _selectionController.IsRunning)
        {
            _boundedScanController.Cancel(
                "Bounded scan blocked: another input operation is running.");
            return;
        }

        _pickerOpenController.Cancel("Picker opener reset for bounded scan.");
        _searchQueryController.Cancel("Query controller reset for bounded scan.");
        _cursorTweenController.Cancel("Cursor tween reset for bounded scan.");
        _selectionController.Cancel("Selection controller reset for bounded scan.");
        _boundedScanController.Start(
            GameController,
            _scanPlan.InitialCollectionSteps,
            _previewStep,
            Settings.PairsPerBoundedScan.Value,
            out _);
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

    private bool IsAnyAutomationRunning =>
        _singlePairScanController.IsRunning || _boundedScanController.IsRunning;

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

    private void CancelSharedInputControllers(string reason)
    {
        _pickerOpenController.Cancel(reason);
        _searchQueryController.Cancel(reason);
        _cursorTweenController.Cancel(reason);
        _selectionController.Cancel(reason);
    }

    private bool StoreAndExportAutomatedSnapshot(
        ExchangePairSnapshot snapshot,
        string source)
    {
        _rateBook.Store(snapshot);
        _captureStatus = $"{source} capture {snapshot.OfferedCurrency.Name} -> " +
            $"{snapshot.WantedCurrency.Name}: {FormatRatio(snapshot.MarketRate)}.";
        try
        {
            var exportedCount = _exporter.Export(
                _rateBook.LatestSnapshots,
                _exportPath);
            _exportStatus = $"Exported {exportedCount} pairs to {_exportPath}";
            return true;
        }
        catch (Exception exception)
        {
            _exportStatus = $"Rate export failed: {exception.Message}";
            return false;
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

    public override void Render()
    {
        Graphics.DrawText(_captureStatus, new Vector2(100, 100), SharpDX.Color.White);
        Graphics.DrawText(
            $"Captured pairs: {_rateBook.LatestSnapshots.Count}",
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
