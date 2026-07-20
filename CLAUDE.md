# ExileAPI Custom Plugin Notes for Claude Code

## Goal

Create a new ExileAPI plugin, tentatively named `FaustusController`, with two responsibilities:

1. Safely generate mouse and keyboard input while Path of Exile is the foreground window.
2. Read and persist Currency Exchange data associated with the Faustus exchange UI.

This folder is a compiled ExileAPI distribution, not a source checkout. The public API was determined from the supplied plugin template, dependency manifests, loaded plugins, and public metadata/decompilation of `ExileCore.dll`.

## Important Findings

- The host is `.NET 10`, Windows x64. See `ExileCore.runtimeconfig.json` and `ExileCore.deps.json`.
- The bundled template still targets `net8.0-windows`. Change the generated plugin to `net10.0-windows` before building against this `ExileCore.dll`.
- Source plugins belong in `Plugins/Source/<PluginName>` with the `.csproj` at the top level of that directory.
- Compiled plugins belong in `Plugins/Compiled/<PluginName>`.
- `config/settings.json` currently has `PreferSourcePlugins=false`. Do not leave a compiled plugin with the same identity in `Plugins/Compiled` while developing the source copy, or enable source preference.
- The plugin template defaults `Enable` to `false`; the plugin must be enabled from the ExileAPI menu after it loads.
- There is no dedicated Faustus UI class. Use `GameController.Game.IngameState.IngameUi.CurrencyExchangePanel` directly.
- Currency mappings are available from in-memory game DAT wrappers. Do not scrape display text or hard-code a currency list unless an API field is missing.
- Save mapping collections as custom JSON under `ConfigDirectory`. Complex collections are not good settings-node values; the current logs already show warnings for unsupported complex plugin settings.
- `ExileCore.dll` is obfuscated. Public type/member signatures are usable, but decompiled method bodies and apparent numeric offsets are often invalid. Never copy raw offsets from decompiler output.

## Repository Landmarks

| Path | Purpose |
| --- | --- |
| `Plugins/howto.txt` | Official short plugin creation instructions |
| `Plugins/exApiTools.Plugin.Template.2.0.0.nupkg` | Supplied project template |
| `ExileCore.dll` | Plugin API and game-memory models |
| `GameOffsets.dll` | Current memory offset structures used by the core |
| `ExileCore.runtimeconfig.json` | Confirms `net10.0` runtime |
| `ExileCore.deps.json` | Confirms `win-x64` and host package versions |
| `Plugins/Compiled/Stashie/Stashie.dll` | Existing mouse/keyboard automation example, but uses legacy APIs |
| `Plugins/Compiled/PickIt/PickIt.dll` | Existing cursor/click example, but has local Win32 code and setting bugs |
| `Plugins/Compiled/BuffUtil/BuffUtil.dll` | `SendInput` keyboard example, but lacks a foreground safety check |
| `Plugins/Compiled/BasicFlaskRoutine/BasicFlaskRoutine.dll` | Window-message keyboard example, but contains a broken key-release method |
| `config/global/*_settings.json` | Serialized settings examples |
| `Logs/Info20260720.log` | Confirms bundled plugin discovery/loading |

The `.gitmodules` file points the historical plugin source submodule at:

```text
https://github.com/qvin0000/exileapiplugins
```

## Create the Project

Run these commands from the distribution root:

```powershell
dotnet new install .\Plugins\exApiTools.Plugin.Template.2.0.0.nupkg
dotnet new exApiPlugin -n FaustusController -o .\Plugins\Source\FaustusController
$env:exapiPackage = (Resolve-Path .).Path
```

The generated project contains:

```text
Plugins/Source/FaustusController/
|-- FaustusController.csproj
|-- FaustusController.cs
`-- FaustusControllerSettings.cs
```

Use this corrected project file as the baseline:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <OutputType>Library</OutputType>
    <UseWindowsForms>true</UseWindowsForms>
    <PlatformTarget>x64</PlatformTarget>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>false</SelfContained>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <DebugType>embedded</DebugType>
    <PathMap>$(MSBuildProjectDirectory)=$(MSBuildProjectName)</PathMap>
    <EmbedAllSources>true</EmbedAllSources>
  </PropertyGroup>

  <ItemGroup>
    <Reference Include="ExileCore">
      <HintPath>$(exapiPackage)\ExileCore.dll</HintPath>
      <Private>False</Private>
    </Reference>
    <Reference Include="GameOffsets">
      <HintPath>$(exapiPackage)\GameOffsets.dll</HintPath>
      <Private>False</Private>
    </Reference>
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="ImGui.NET" Version="1.90.0.1" />
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
    <PackageReference Include="SharpDX.Mathematics" Version="4.2.0" />
  </ItemGroup>
</Project>
```

The HUD supplies its own output path when compiling projects under `Plugins/Source`. For a direct compile, run:

```powershell
dotnet build .\Plugins\Source\FaustusController\FaustusController.csproj
```

If the HUD compilation fails, inspect its log and any generated `Errors.txt`. The source compiler uses `Restore` and `Build`, and caches results in a compilation manifest. The ExileAPI menu has a compilation-cache reset if stale output is loaded.

## Plugin Contract

The normal main class is:

```csharp
public sealed class FaustusController
    : BaseSettingsPlugin<FaustusControllerSettings>
```

The settings class must implement `ISettings` and must expose `Enable`:

```csharp
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using System.Windows.Forms;

namespace FaustusController;

public sealed class FaustusControllerSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new(false);
    public ToggleNode RequireForeground { get; set; } = new(true);
    public ToggleNode DryRun { get; set; } = new(true);
    public HotkeyNodeV2 CaptureMappings { get; set; } = new(Keys.F6);
    public HotkeyNodeV2 RunExchangeAction { get; set; } = new(Keys.F7);
    public HotkeyNodeV2 KillSwitch { get; set; } = new(Keys.Pause);
    public HotkeyNodeV2 OutputKey { get; set; } = new(Keys.Space);
    public RangeNode<int> InputDelayMs { get; set; } = new(40, 5, 500);
}
```

Confirmed lifecycle methods on `BaseSettingsPlugin<TSettings>` include:

```csharp
public override void OnLoad();
public override bool Initialise();
public override void AreaChange(AreaInstance area);
public override Job Tick();
public override void Render();
public override void EntityAdded(Entity entity);
public override void EntityRemoved(Entity entity);
public override void ReceiveEvent(string eventId, object args);
public override void OnClose();
public override void OnUnload();
```

Use the lifecycle this way:

| Method | Intended work |
| --- | --- |
| `Initialise` | Build paths, load custom JSON, register/change hotkeys if necessary |
| `AreaChange` | Cancel or reset any pending UI operation and discard stale element references |
| `Tick` | Check state/hotkeys and advance a non-blocking operation |
| `Render` | ImGui and drawing only; never sleep or perform long disk work |
| `OnClose`/`OnUnload` | Cancel operations and save dirty custom data |

`Tick()` and `Render()` are called every frame. Do not use `Thread.Sleep` in either one. Use an explicit state machine, a coroutine/`Job`, or a guarded `Task` with cancellation.

Useful base properties include:

```csharp
GameController GameController
Graphics Graphics
PluginManager PluginManager
string ConfigDirectory
CancellationToken ZoneCancellationToken
```

## Mouse and Keyboard APIs

### Hotkey Detection

Use `HotkeyNodeV2`, not obsolete `HotkeyNode`:

```csharp
if (Settings.CaptureMappings.PressedOnce())
{
    CaptureMappings();
}
```

Confirmed `HotkeyNodeV2` methods:

```csharp
bool PressedOnce();
bool IsPressed();
bool UnpressedOnce();
bool DrawPickerButton(string id);
```

Confirmed constructors include:

```csharp
new HotkeyNodeV2();
new HotkeyNodeV2(Keys key);
new HotkeyNodeV2(HotkeyNodeV2.HotkeyNodeValue value);
```

If hotkey polling does not fire in the first compile, explicitly register the value in `Initialise` and re-register it from `OnValueChanged`:

```csharp
Input.RegisterKey(Settings.CaptureMappings.Value);
Settings.CaptureMappings.OnValueChanged +=
    () => Input.RegisterKey(Settings.CaptureMappings.Value);
```

Do not add this registration speculatively if `PressedOnce()` already works; the current node may be registered by the settings parser.

### Keyboard Generation

Prefer the current helper when sending a configured hotkey value:

```csharp
using ExileCore.Shared.Helpers;

InputHelper.SendInputPress(Settings.OutputKey.Value);
InputHelper.SendInputDown(Settings.OutputKey.Value);
InputHelper.SendInputUp(Settings.OutputKey.Value);
```

All three methods return `bool`. Check the result and log failures.

Legacy static methods also exist:

```csharp
Input.KeyDown(Keys key);
Input.KeyUp(Keys key);
Input.KeyPressRelease(Keys key);
Input.KeyDown(Keys key, nint windowHandle);
Input.KeyUp(Keys key, nint windowHandle);
```

Prefer explicit down/up pairs with `try/finally` for held keys. Do not copy `BasicFlaskRoutine.KeyPressRelease`; that plugin's method presses a key but never releases it.

### Mouse Generation

The newer movement surface is available through `Input.InputManager`:

```csharp
bool moved = Input.InputManager.MoveMouse(screenCoordinate);

await Input.InputManager.MoveMouseAsync(
    stroke,
    cancellationToken);
```

Confirmed manager methods:

```csharp
IStatusDisposable BlockUserMouseInput();
IStatusDisposable BlockUserKeyboardInput();
bool MoveMouse(System.Numerics.Vector2 coordinate);
Task<bool> MoveMouseAsync(
    MouseMoveStroke stroke,
    CancellationToken cancellationToken = default);
SyncTask<bool> MoveMouseSyncTask(
    MouseMoveStroke stroke,
    CancellationToken cancellationToken = default);
```

Movement strokes use:

```csharp
new MouseMoveStroke(new List<MouseMoveStrokePoint>
{
    new(target, TimeSpan.FromMilliseconds(40))
});
```

`MouseMoveStrokePoint` has `Point` and `Delay` values. Confirm the behavior of a one-point stroke in a compile/runtime smoke test before building a humanized multi-point path.

Clicks and wheel actions are on the static API:

```csharp
Input.Click(MouseButtons.Left);
Input.Click(MouseButtons.Right);
Input.LeftDown();
Input.LeftUp();
Input.RightDown();
Input.RightUp();
Input.VerticalScroll(forward: true, clicks: 1);
```

Legacy direct movement also exists:

```csharp
Input.SetCursorPos(System.Numerics.Vector2 position);
```

Prefer `Input.InputManager.MoveMouse` for new code, then use `Input.Click` for the button action.

### Input Safety Rules

Implement all of these before any real exchange automation:

1. Require `GameController.Window.IsForeground()` immediately before every generated input step.
2. Require the expected panel to be visible immediately before every generated input step.
3. Abort on area change, panel close, Escape, plugin disable, or cancellation.
4. Never keep an `Element` reference across an area change; reacquire the panel and child elements.
5. Never hold mouse/keyboard blocking across frames. If blocking is used, wrap the smallest operation in `using` or `try/finally` and inspect `IStatusDisposable.IsSuccess`.
6. Never leave a key or mouse button down after an exception or cancellation.
7. Serialize automation so only one operation can run at a time.
8. Add a dry-run mode that logs/draws target points without generating input.
9. Add a kill-switch hotkey that is independent of the action hotkey.
10. Rate-limit actions and add a short revalidation delay before clicking.

Element rectangles and input movement must use the same coordinate space. Validate this by drawing the intended target point first. The distribution README warns that Windows scaling usually needs to be 100%, so DPI scaling must be tested rather than assumed.

## Currency Exchange API

### Primary Access Paths

```csharp
var ingameState = GameController.Game.IngameState;
var panel = ingameState.IngameUi.CurrencyExchangePanel;
var placedOrders = ingameState.ServerData.PlacedCurrencyExchangeOrders;

var exchangeEntries = GameController.Files.CurrencyExchange.EntriesList;
var exchangeCategories = GameController.Files.CurrencyExchangeCategories.EntriesList;
var currencyItems = GameController.Files.CurrencyItems.EntriesList;
var baseItemTypes = GameController.Files.BaseItemTypes;
```

`UniversalFileWrapper<T>.EntriesList` is the intended way to enumerate DAT records. Do not reach into its private/protected caches.

### Exchange Catalogue Types

`CurrencyExchangeEntry` exposes:

```csharp
BaseItemType BaseItemType { get; }
CurrencyExchangeCategory Category1 { get; }
CurrencyExchangeCategory Category2 { get; }
byte Byte1 { get; }
byte Byte2 { get; }
```

`CurrencyExchangeCategory` exposes:

```csharp
string Name { get; }
string DisplayName { get; }
```

`CurrencyItemDat` exposes:

```csharp
BaseItemType Type { get; }
int StackSize { get; }
string Action { get; }
string Description { get; }
```

`BaseItemType` exposes the durable identity/display fields needed here:

```csharp
string Metadata { get; set; }
string ClassName { get; set; }
string BaseName { get; set; }
uint Hash { get; set; }
```

`BaseItemTypes` provides:

```csharp
Dictionary<string, BaseItemType> Contents { get; }
BaseItemType GetByHash(uint hash);
BaseItemType Translate(string metadata);
```

Use `Metadata` as the persisted primary key. Store `Hash` for fast correlation with current runtime orders, but do not treat the hash as the only durable key across game patches.

### Live Panel Types

`CurrencyExchangePanel` exposes:

```csharp
BaseItemType OfferedItemType { get; }
BaseItemType WantedItemType { get; }
List<CurrencyExchangeStock> WantedItemStock { get; }
List<CurrencyExchangeStock> OfferedItemStock { get; }
short MarketRateGet { get; }
short MarketRateGive { get; }
CurrencyExchangeCurrencyPickerElement CurrencyPicker { get; }
List<CurrencyExchangePanelOrderElement> OrderElements { get; }
List<PlacedCurrencyExchangeOrder> Orders { get; }
Element OfferedItemCountInput { get; }
Element WantedItemCountInput { get; }
Element RatioElement { get; }
```

The picker exposes:

```csharp
bool IsPickingWantedCurrency { get; }
Element OptionContainer { get; }
List<CurrencyExchangeCurrencyPickerCurrencyOption> Options { get; }
```

Each picker option exposes:

```csharp
BaseItemType ItemType { get; }
int Owned { get; }
```

Each stock row exposes:

```csharp
int Get { get; }
int Give { get; }
int ListedCount { get; }
```

Use the panel properties instead of traversing child indexes. The picker currently uses an internal child index, but that is an implementation detail and likely to change.

### Placed Orders

`PlacedCurrencyExchangeOrder` exposes:

```csharp
DateTimeOffset CreationDate { get; }
int PlayerOrderId { get; }
uint OfferedItemHash { get; }
BaseItemType OfferedItemType { get; }
uint WantedItemHash { get; }
BaseItemType WantedItemType { get; }
int OriginalOfferedItemStackSize { get; }
int OfferedItemStackSize { get; }
int WantedItemStackSize { get; }
int GoldCost { get; }
int OfferedItemRatioPart { get; }
int WantedItemRatioPart { get; }
int CompetingOfferedItemRatioPart { get; }
int CompetingWantedItemRatioPart { get; }
bool IsCompleted { get; }
bool IsCanceled { get; }
```

Prefer `OfferedItemType` and `WantedItemType`. If either is unavailable, resolve the hash through `GameController.Files.BaseItemTypes.GetByHash(...)`.

## Mapping Storage Design

Do not put the catalogue in `FaustusControllerSettings`. Keep settings small and use a versioned custom file such as:

```csharp
_mappingPath = Path.Combine(ConfigDirectory, "currency-mappings.json");
```

Recommended schema:

```csharp
public sealed class CurrencyMappingFile
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public List<CurrencyMapping> Items { get; set; } = [];
}

public sealed class CurrencyMapping
{
    public string Metadata { get; set; } = "";
    public uint Hash { get; set; }
    public string BaseName { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string? PrimaryCategory { get; set; }
    public string? PrimaryCategoryDisplayName { get; set; }
    public string? SecondaryCategory { get; set; }
    public string? SecondaryCategoryDisplayName { get; set; }
    public int? StackSize { get; set; }
    public string? Action { get; set; }
    public string? Description { get; set; }
}
```

Keep live observations in a separate file or section because owned amounts, selected items, market stocks, and ratios are transient:

```csharp
public sealed class CurrencyPickerObservation
{
    public DateTimeOffset CapturedAtUtc { get; set; }
    public bool IsPickingWantedCurrency { get; set; }
    public List<CurrencyPickerOptionObservation> Options { get; set; } = [];
}

public sealed class CurrencyPickerOptionObservation
{
    public string Metadata { get; set; } = "";
    public uint Hash { get; set; }
    public string BaseName { get; set; } = "";
    public int Owned { get; set; }
}
```

### Catalogue Join Algorithm

1. Read `CurrencyItems.EntriesList` and index records by `Type.Metadata`.
2. Enumerate `CurrencyExchange.EntriesList`.
3. Skip records whose `BaseItemType` or `BaseItemType.Metadata` is null/empty.
4. Use `BaseItemType.Metadata` as the dictionary key.
5. Copy `Hash`, `BaseName`, and `ClassName` from `BaseItemType`.
6. Copy category names/display names from `Category1` and `Category2` with null checks.
7. Join optional `StackSize`, `Action`, and `Description` from the matching `CurrencyItemDat` record.
8. De-duplicate by metadata and sort deterministically by category, base name, then metadata.
9. Write to a temporary file in `ConfigDirectory`, then atomically replace the destination where practical.
10. Log item count, duplicate count, skipped count, and output path.

JSON save/load can use the host-matching Newtonsoft.Json package:

```csharp
var json = JsonConvert.SerializeObject(data, Formatting.Indented);
File.WriteAllText(_mappingPath, json);

var data = JsonConvert.DeserializeObject<CurrencyMappingFile>(
    File.ReadAllText(_mappingPath));
```

Disk I/O should occur only when a capture is requested, data is dirty, or the plugin closes. Do not serialize every frame.

## Suggested Implementation Order

1. Generate the template project and change it to `net10.0-windows`.
2. Build an empty plugin and confirm it appears in the ExileAPI menu.
3. Add settings and confirm their JSON is written under `config/global`.
4. Add a read-only debug view for the exchange panel and current offered/wanted item names.
5. Enumerate the three DAT wrappers and log counts only.
6. Implement the catalogue join and save `currency-mappings.json`.
7. Add a picker observation capture and save it separately.
8. Add dry-run target visualization for mouse actions.
9. Add one foreground-gated test keypress.
10. Add one foreground/panel-gated test mouse move without clicking.
11. Add clicking only after coordinates and cancellation behavior are verified.
12. Implement the final exchange workflow as a cancelable state machine with revalidation before each input.

## Existing Plugin Review Warnings

Do not blindly copy the bundled automation plugins:

| Plugin | Useful lesson | Problem to avoid |
| --- | --- | --- |
| `Stashie` | Shows cursor movement, clicks, key down/up, scrolling, and hotkeys | Uses obsolete `HotkeyNode` and legacy movement APIs; its `BlockInput` setting is not operationally used |
| `PickIt` | Shows element-targeted cursor/click flow | Uses a local Win32 wrapper; several UI settings are wired incorrectly or never consumed |
| `BuffUtil` | Shows `SendInput` through InputSimulatorCore | Does not verify that Path of Exile is foreground before generating global input |
| `BasicFlaskRoutine` | Shows window-message key input and foreground gating | Its `KeyPressRelease` implementation never sends key-up |

The new plugin should use the core API and should centralize all input in one service/class so every action gets the same foreground, panel, cancellation, and release guarantees.

## Acceptance Criteria

- The project builds against this distribution with .NET 10 and x64.
- The HUD discovers the source plugin without copying `ExileCore.dll` or `GameOffsets.dll` into the plugin folder.
- The plugin starts disabled and exposes configurable action, capture, and kill-switch hotkeys.
- No input is generated when Path of Exile is not foreground.
- No exchange input is generated when `CurrencyExchangePanel` is not visible.
- Area changes and plugin disable cancel pending work and release all held inputs.
- The generated mapping file is versioned, deterministic, and keyed by `BaseItemType.Metadata`.
- The mapping includes current hashes for placed-order correlation but does not use hashes as the only persisted identity.
- Static catalogue data and transient picker/stock/order observations are stored separately.
- The implementation does not hard-code memory offsets or depend on internal UI child indexes.
- The implementation does not block `Tick` or `Render` with sleeps or synchronous long-running work.
- A dry-run mode visualizes intended targets before mouse clicks are enabled.

## API Uncertainties to Resolve by Compiling

The public signatures above are confirmed for this binary, but Claude Code should use short compile checkpoints because the core is obfuscated and this distribution has no matching source tree.

Verify these points in this order:

1. Whether `HotkeyNodeV2.PressedOnce()` works without explicit `Input.RegisterKey`.
2. The exact visibility property inherited by `CurrencyExchangePanel` in this build, normally `IsVisible`.
3. The exact rectangle/center API to use for exchange elements and whether coordinates are already desktop coordinates.
4. Whether a one-point `MouseMoveStroke` delays before or after moving.
5. Whether DAT wrappers are populated during `Initialise`; if not, defer mapping capture until `Tick` sees non-empty `EntriesList` values.

Treat compile errors as API discovery feedback. Do not compensate by adding reflection, hard-coded offsets, or raw process-memory reads unless a required public property is genuinely unavailable.
