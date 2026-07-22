using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using System.Windows.Forms;

namespace FaustusController;

public sealed class FaustusControllerSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new(false);
    public ToggleNode AllowSearchFocusInput { get; set; } = new(false);
    public ToggleNode AllowSearchQueryInput { get; set; } = new(false);
    public ToggleNode AllowVerifiedTargetMouseMove { get; set; } = new(false);
    public ToggleNode AllowVerifiedOptionClick { get; set; } = new(false);
    public ToggleNode AllowCalibratedPickerOpen { get; set; } = new(false);
    public ToggleNode AllowSinglePairAutomation { get; set; } = new(false);
    public ToggleNode AllowBoundedScanAutomation { get; set; } = new(false);
    public ToggleNode AllowLiquidityDiscoveryAutomation { get; set; } = new(false);
    public ToggleNode AllowActiveRefreshAutomation { get; set; } = new(false);
    public RangeNode<int> PairsPerBoundedScan { get; set; } = new(2, 1, 10);
    public RangeNode<int> MaximumQuoteAgeMinutes { get; set; } = new(15, 1, 1440);
    public RangeNode<int> CursorTweenSpeed { get; set; } = new(1600, 400, 4000);
    public HotkeyNodeV2 RunRouteAnalysis { get; set; } = new(Keys.Home)
    {
        IgnoreFocusedInput = true
    };
    public HotkeyNodeV2 RunBoundedScanAutomation { get; set; } = new(Keys.F3)
    {
        IgnoreFocusedInput = true
    };
    public HotkeyNodeV2 RunLiquidityDiscoveryAutomation { get; set; } = new(Keys.F2)
    {
        IgnoreFocusedInput = true
    };
    public HotkeyNodeV2 RunActiveRefreshAutomation { get; set; } = new(Keys.Insert)
    {
        IgnoreFocusedInput = true
    };
    public HotkeyNodeV2 RunSinglePairAutomation { get; set; } = new(Keys.F4)
    {
        IgnoreFocusedInput = true
    };
    public HotkeyNodeV2 OpenNextPlannedPicker { get; set; } = new(Keys.F5)
    {
        IgnoreFocusedInput = true
    };
    public HotkeyNodeV2 CaptureCurrentRate { get; set; } = new(Keys.F6);
    public HotkeyNodeV2 PreviewNextScanStep { get; set; } = new(Keys.F7);
    public HotkeyNodeV2 FocusPickerSearch { get; set; } = new(Keys.F8)
    {
        IgnoreFocusedInput = true
    };
    public HotkeyNodeV2 EnterPickerSearchQuery { get; set; } = new(Keys.F9)
    {
        IgnoreFocusedInput = true
    };
    public HotkeyNodeV2 MoveToVerifiedOption { get; set; } = new(Keys.F10)
    {
        IgnoreFocusedInput = true
    };
    public HotkeyNodeV2 SelectVerifiedOption { get; set; } = new(Keys.F11)
    {
        IgnoreFocusedInput = true
    };
    public HotkeyNodeV2 CalibratePickerButtons { get; set; } = new(Keys.F12)
    {
        IgnoreFocusedInput = true
    };
}
