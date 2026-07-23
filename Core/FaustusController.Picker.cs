using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using Newtonsoft.Json;
using System.Windows.Forms;
using Vector2 = System.Numerics.Vector2;

namespace FaustusController;

public sealed partial class FaustusController
{
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
}
