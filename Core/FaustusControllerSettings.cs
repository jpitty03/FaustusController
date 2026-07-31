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
    // Verified order collection: ctrl-right-click completed exchange orders into
    // inventory, then ctrl+shift+right-click each collected currency into stash.
    // Needs the exchange, stash, and inventory all open; only clicks inventory
    // stacks not covered by the exchange window (block the covered left columns).
    public ToggleNode AllowOrderCollection { get; set; } = new(false);
    public HotkeyNodeV2 CollectOrdersKey { get; set; } = new(Keys.F11)
    {
        IgnoreFocusedInput = true
    };
    // Verified multi-hop route execution: chains the selected route's hops
    // (execute -> wait for fill -> collect -> next). The most autonomous action;
    // requires every single-hop-execution and order-collection permission too.
    public ToggleNode AllowMultiHopExecution { get; set; } = new(false);
    public HotkeyNodeV2 MultiHopExecuteKey { get; set; } = new(Keys.F5)
    {
        IgnoreFocusedInput = true
    };
    // How far (%) the live market rate may fall below a hop's analysis-planned rate
    // and still execute. 0 = only trade when the live rate is at least as good as
    // planned (a better rate always recomputes up and proceeds); raise it to tolerate
    // small adverse drift. Applies to F10 and every F5 hop.
    public RangeNode<int> MaxRateSlippagePercent { get; set; } = new(0, 0, 100);
    // Extra permission required to STAGE or PLACE a resting-limit (maker) hop with F6/F8/F10.
    // Immediate hops never need it. Default off; a maker route cannot move the cursor or type
    // until this is enabled alongside the usual staging/placement toggles.
    public ToggleNode AllowCompetingOrderExecution { get; set; } = new(false);
}
