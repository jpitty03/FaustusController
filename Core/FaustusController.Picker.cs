using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using Newtonsoft.Json;
using System.Windows.Forms;
using Vector2 = System.Numerics.Vector2;

namespace FaustusController;

public sealed partial class FaustusController
{
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
}
