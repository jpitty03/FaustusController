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
    // How many consecutive identical rate reads a single-pair scan needs
    // before capturing. 1 captures the first observed rate immediately.
    public RangeNode<int> StableRateSampleCount { get; set; } = new(3, 1, 5);
    // Tradable categories included in discovery/scanning. Disabling a category
    // acts like ForceSkip for every catalogue item in it, except Chaos Orb and
    // Divine Orb (pivots) and anything manually listed in ForceInclude.
    public ToggleNode IncludeDivinationCards { get; set; } = new(true);
    public ToggleNode IncludeCurrency { get; set; } = new(true);
    public ToggleNode IncludeDeliriumOrbs { get; set; } = new(true);
    public ToggleNode IncludeScarabs { get; set; } = new(true);
    public ToggleNode IncludeFossils { get; set; } = new(true);
    public ToggleNode IncludeEssences { get; set; } = new(true);
    public ToggleNode IncludeOtherTradables { get; set; } = new(true);
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
    public ToggleNode AllowOrderPlacement { get; set; } = new(false);
    public HotkeyNodeV2 PlaceOrderKey { get; set; } = new(Keys.F8)
    {
        IgnoreFocusedInput = true
    };
    public HotkeyNodeV2 CalibratePlaceOrderButtonKey { get; set; } = new(Keys.F9)
    {
        IgnoreFocusedInput = true
    };
    // Verified single-hop execution: F8 placement plus an explicit pre-execution
    // rate gate and a post-execution actual-vs-expected audit. Requires every
    // order-placement permission in addition to this toggle.
    public ToggleNode AllowSingleHopExecution { get; set; } = new(false);
    public HotkeyNodeV2 ExecuteHopKey { get; set; } = new(Keys.F10)
    {
        IgnoreFocusedInput = true
    };
}
