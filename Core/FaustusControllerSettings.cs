using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using System.Windows.Forms;

namespace FaustusController;

public sealed class FaustusControllerSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new(false);
    public ToggleNode AllowSearchQueryInput { get; set; } = new(false);
    public ToggleNode AllowVerifiedTargetMouseMove { get; set; } = new(false);
    public ToggleNode AllowVerifiedOptionClick { get; set; } = new(false);
    public ToggleNode AllowCalibratedPickerOpen { get; set; } = new(false);
    public ToggleNode AllowLiquidityDiscoveryAutomation { get; set; } = new(false);
    public ToggleNode AllowActiveRefreshAutomation { get; set; } = new(false);
    public ToggleNode AllowOrderAmountInput { get; set; } = new(false);
    public RangeNode<int> MaximumQuoteAgeMinutes { get; set; } = new(15, 1, 1440);
    public RangeNode<int> CursorTweenSpeed { get; set; } = new(1600, 400, 4000);
    public HotkeyNodeV2 RunRouteAnalysis { get; set; } = new(Keys.Home)
    {
        IgnoreFocusedInput = true
    };
    public HotkeyNodeV2 CycleRouteUp { get; set; } = new(Keys.PageUp)
    {
        IgnoreFocusedInput = true
    };
    public HotkeyNodeV2 CycleRouteDown { get; set; } = new(Keys.PageDown)
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
    public HotkeyNodeV2 CalibratePickerButtons { get; set; } = new(Keys.F12)
    {
        IgnoreFocusedInput = true
    };
    public HotkeyNodeV2 SyncInventoryFromPicker { get; set; } = new(Keys.NumPad7)
    {
        IgnoreFocusedInput = true
    };
    public HotkeyNodeV2 ExportPlacedOrders { get; set; } = new(Keys.NumPad8)
    {
        IgnoreFocusedInput = true
    };
    public HotkeyNodeV2 CalibrateGoldCost { get; set; } = new(Keys.NumPad9)
    {
        IgnoreFocusedInput = true
    };
    // F-keys on purpose: NumPad digits are character keys, so pressing them
    // while a count input is still focused types a stray digit into the field.
    public HotkeyNodeV2 TypeOfferedAmountKey { get; set; } = new(Keys.F3)
    {
        IgnoreFocusedInput = true
    };
    public HotkeyNodeV2 TypeWantedAmountKey { get; set; } = new(Keys.F4)
    {
        IgnoreFocusedInput = true
    };
    public ToggleNode AllowOrderStaging { get; set; } = new(false);
    public HotkeyNodeV2 StageOrderDryRunKey { get; set; } = new(Keys.F6)
    {
        IgnoreFocusedInput = true
    };
    public HotkeyNodeV2 DumpSdkReadsKey { get; set; } = new(Keys.F7)
    {
        IgnoreFocusedInput = true
    };
}
