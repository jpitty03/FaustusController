using System.Text;
using ExileCore.PoEMemory;

namespace FaustusController;

public sealed partial class FaustusController
{
    private string _sdkProbeStatus =
        "SDK probe idle: open the exchange panel and press the probe hotkey (F7).";

    /// <summary>
    /// Dumps every ExileCore read this plugin depends on to a text file. After an
    /// ExileApi update the plugin can compile cleanly while the offsets behind the
    /// unchanged member names read garbage; this probe records the raw values so
    /// the first bad read is identified from facts instead of guesses. Run it once
    /// with only the exchange panel open and once with a picker open.
    /// </summary>
    private void DumpSdkReads()
    {
        var report = new StringBuilder();
        var issues = new List<string>();
        report.AppendLine($"FaustusController SDK read probe at {DateTimeOffset.UtcNow:O}");

        static string Describe(string label, Element? element)
        {
            if (element == null)
            {
                return $"{label}: null";
            }

            var rectangle = element.GetClientRectCache;
            return $"{label}: visible={element.IsVisible} active={element.IsActive} " +
                $"rect=({rectangle.X:F0},{rectangle.Y:F0} {rectangle.Width:F0}x{rectangle.Height:F0})";
        }

        void Section(string name, Action body)
        {
            report.AppendLine($"-- {name}");
            try
            {
                body();
            }
            catch (Exception exception)
            {
                report.AppendLine($"   EXCEPTION {exception.GetType().Name}: {exception.Message}");
                issues.Add($"{name} threw {exception.GetType().Name}");
            }
        }

        var panel = GameController.Game.IngameState.IngameUi.CurrencyExchangePanel;

        Section("Panel", () => report.AppendLine("   " + Describe("panel", panel)));

        Section("Selected pair", () =>
        {
            var offered = panel.OfferedItemType;
            var wanted = panel.WantedItemType;
            report.AppendLine(
                $"   offered: {(offered == null ? "null" : $"'{offered.BaseName}' ({offered.Metadata})")}");
            report.AppendLine(
                $"   wanted: {(wanted == null ? "null" : $"'{wanted.BaseName}' ({wanted.Metadata})")}");
        });

        Section("Market rate", () =>
            report.AppendLine($"   MarketRateGet={panel.MarketRateGet} MarketRateGive={panel.MarketRateGive}"));

        Section("Stock", () =>
        {
            var wantedStock = panel.WantedItemStock;
            var offeredStock = panel.OfferedItemStock;
            report.AppendLine($"   WantedItemStock: {(wantedStock == null ? "null" : wantedStock.Count.ToString())}");
            foreach (var level in (wantedStock ?? []).Take(5))
            {
                report.AppendLine($"      get={level.Get} give={level.Give} listed={level.ListedCount}");
            }

            report.AppendLine($"   OfferedItemStock: {(offeredStock == null ? "null" : offeredStock.Count.ToString())}");
            foreach (var level in (offeredStock ?? []).Take(5))
            {
                report.AppendLine($"      get={level.Get} give={level.Give} listed={level.ListedCount}");
            }
        });

        Section("Count inputs", () =>
        {
            var offeredInput = panel.OfferedItemCountInput;
            var wantedInput = panel.WantedItemCountInput;
            report.AppendLine("   " + Describe("OfferedItemCountInput", offeredInput));
            report.AppendLine(
                $"      digits='{(offeredInput == null ? "" : CurrencyAmountInputController.ReadDigits(offeredInput))}'");
            report.AppendLine("   " + Describe("WantedItemCountInput", wantedInput));
            report.AppendLine(
                $"      digits='{(wantedInput == null ? "" : CurrencyAmountInputController.ReadDigits(wantedInput))}'");
        });

        var optionCount = -1;
        var resolvableCount = 0;
        Section("Picker", () =>
        {
            var picker = panel.CurrencyPicker;
            report.AppendLine("   " + Describe("CurrencyPicker", picker));
            report.AppendLine($"   IsPickingWantedCurrency={picker.IsPickingWantedCurrency}");
            report.AppendLine("   " + Describe("OptionContainer", picker.OptionContainer));

            var options = picker.Options;
            optionCount = options?.Count ?? -1;
            report.AppendLine($"   Options: {(options == null ? "null" : options.Count.ToString())}");
            foreach (var option in (options ?? []).Take(10))
            {
                var item = option?.ItemType;
                var resolves = item != null &&
                    !string.IsNullOrWhiteSpace(item.Metadata) &&
                    _catalogue != null &&
                    _catalogue.TryGetByMetadata(item.Metadata, out _);
                var nameMatch = !resolves && option != null && _catalogue != null
                    ? CurrencyPickerInspector.TryResolveByText(option, _catalogue)
                    : null;
                var rectangle = option?.GetClientRectCache;
                report.AppendLine(
                    $"      {(item == null ? "itemType=null" : $"'{item.BaseName}' ({item.Metadata})")} " +
                    $"owned={option?.Owned} visible={option?.IsVisible} " +
                    $"rect={(rectangle == null ? "-" : $"{rectangle.Value.Width:F0}x{rectangle.Value.Height:F0}")} " +
                    $"catalogue={(resolves ? "resolves" : nameMatch != null ? $"BY NAME '{nameMatch.Name}'" : "NO MATCH")}");
            }

            foreach (var option in (options ?? []).Take(3))
            {
                report.AppendLine("      -- option child texts:");
                foreach (var text in CurrencyPickerInspector.EnumerateTexts(option, 0).Take(8))
                {
                    report.AppendLine($"         '{text}'");
                }
            }

            if (options != null)
            {
                resolvableCount = options.Take(300).Count(candidate =>
                    candidate != null &&
                    _catalogue != null &&
                    ((candidate.ItemType != null &&
                        !string.IsNullOrWhiteSpace(candidate.ItemType.Metadata) &&
                        _catalogue.TryGetByMetadata(candidate.ItemType.Metadata, out _)) ||
                        CurrencyPickerInspector.TryResolveByText(candidate, _catalogue) != null));
            }
        });

        Section("Orders", () =>
        {
            report.AppendLine($"   Orders: {(panel.Orders == null ? "null" : panel.Orders.Count.ToString())}");
            report.AppendLine(
                $"   OrderElements: {(panel.OrderElements == null ? "null" : panel.OrderElements.Count.ToString())}");
            report.AppendLine("   " + Describe("RatioElement", panel.RatioElement));
        });

        var path = Path.Combine(ConfigDirectory, "FaustusController_sdk-probe.txt");
        try
        {
            File.WriteAllText(path, report.ToString());
            _sdkProbeStatus = $"SDK probe: picker options={optionCount} " +
                $"(catalogue matches={resolvableCount})" +
                (issues.Count > 0 ? $"; ISSUES: {string.Join("; ", issues)}" : "; no read exceptions") +
                $"; wrote {path}";
        }
        catch (Exception exception)
        {
            _sdkProbeStatus = $"SDK probe failed to write '{path}': {exception.Message}";
        }
    }
}
