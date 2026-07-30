using System.Text;
using ExileCore.PoEMemory;
using ExileCore.Shared.Enums;

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

        // Order collection (ctrl-click a completed order into stash) needs the
        // exchange, stash, and inventory all open. Dump all three so the F11
        // collector's window gate can be built from confirmed reads.
        Section("Windows", () =>
        {
            var ingameUi = GameController.Game.IngameState.IngameUi;
            report.AppendLine("   " + Describe("CurrencyExchangePanel", panel));
            report.AppendLine("   " + Describe("StashElement", ingameUi.StashElement));
            report.AppendLine("   " + Describe("InventoryPanel", ingameUi.InventoryPanel));
        });

        // Phase 2 of collection (inventory -> stash via ctrl+shift+right-click) must
        // only click stacks that are NOT covered by the exchange window's right edge.
        // Dump every visible inventory item's screen rect + grid pos + metadata and
        // flag covered ones, so the collector's covered-cell logic is built from facts.
        Section("Inventory grid", () =>
        {
            var ingameUi = GameController.Game.IngameState.IngameUi;
            var inventoryPanel = ingameUi.InventoryPanel;
            report.AppendLine("   " + Describe("InventoryPanel", inventoryPanel));
            var inventory = inventoryPanel?[InventoryIndex.PlayerInventory];
            if (inventory == null)
            {
                report.AppendLine("   Inventory: null");
                return;
            }

            var server = inventory.ServerInventory;
            report.AppendLine($"   ServerInventory: Columns={server?.Columns} Rows={server?.Rows}");
            var exchangeRight = panel.GetClientRectCache.Right;
            report.AppendLine(
                $"   Exchange right edge X={exchangeRight:F0} " +
                "(inventory cells with Left < this are COVERED by the exchange window)");
            var items = inventory.VisibleInventoryItems;
            report.AppendLine(
                $"   VisibleInventoryItems: {(items == null ? "null" : items.Count.ToString())}");
            var itemCount = items?.Count ?? 0;
            var coveredCount = 0;
            for (var i = 0; i < itemCount && i < 60; i++)
            {
                var item = items![i];
                if (item == null)
                {
                    report.AppendLine($"      <{i}> null");
                    continue;
                }

                var rectangle = item.GetClientRectCache;
                var covered = rectangle.Left < exchangeRight;
                if (covered)
                {
                    coveredCount++;
                }

                report.AppendLine(
                    $"      <{i}> rect=({rectangle.X:F0},{rectangle.Y:F0} " +
                    $"{rectangle.Width:F0}x{rectangle.Height:F0}) " +
                    $"{(covered ? "COVERED" : "visible")} '{item.Item?.Path ?? ""}'");
            }

            report.AppendLine(
                $"   => {coveredCount} of {itemCount} visible items are covered by the exchange window.");
        });

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

        var orderCount = -1;
        var orderElementCount = -1;
        Section("Orders", () =>
        {
            var orders = panel.Orders;
            var orderElements = panel.OrderElements;
            orderCount = orders?.Count ?? -1;
            orderElementCount = orderElements?.Count ?? -1;
            report.AppendLine($"   Orders: {(orders == null ? "null" : orders.Count.ToString())}");
            report.AppendLine(
                $"   OrderElements: {(orderElements == null ? "null" : orderElements.Count.ToString())} " +
                "(equal count => parallel to Orders)");
            report.AppendLine("   " + Describe("RatioElement", panel.RatioElement));

            // Recursively dumps an order element's sub-tree (rects/text/texture) so
            // Part B can pick the exact bought-currency icon to ctrl-click.
            void DumpChild(Element? child, int index, string indent, int depth)
            {
                if (child == null)
                {
                    report.AppendLine($"{indent}<{index}> null");
                    return;
                }

                var rectangle = child.GetClientRectCache;
                report.AppendLine(
                    $"{indent}<{index}> rect=({rectangle.X:F0},{rectangle.Y:F0} " +
                    $"{rectangle.Width:F0}x{rectangle.Height:F0}) vis={child.IsVisible} " +
                    $"childCount={child.ChildCount} text='{child.TextNoTags}' " +
                    $"texture='{child.TextureName}'");
                if (depth <= 0)
                {
                    return;
                }

                var kids = child.Children;
                if (kids == null)
                {
                    return;
                }

                for (var k = 0; k < kids.Count && k < 8; k++)
                {
                    DumpChild(kids[k], k, indent + "   ", depth - 1);
                }
            }

            var count = orders?.Count ?? 0;
            for (var i = 0; i < count && i < 12; i++)
            {
                var order = orders![i];
                if (order == null)
                {
                    report.AppendLine($"   [{i}] order=null");
                    continue;
                }

                var status = order.IsCanceled
                    ? "Canceled"
                    : order.IsCompleted
                        ? "Completed"
                        : "Pending";
                report.AppendLine(
                    $"   [{i}] id={order.PlayerOrderId} {status} " +
                    $"'{order.OfferedItemType?.BaseName ?? "?"}' -> " +
                    $"'{order.WantedItemType?.BaseName ?? "?"}' " +
                    $"wantedStack={order.WantedItemStackSize} " +
                    $"offered={order.OfferedItemStackSize}/{order.OriginalOfferedItemStackSize}");

                if (orderElements == null || i >= orderElements.Count)
                {
                    report.AppendLine("        element: <no parallel OrderElements entry>");
                    continue;
                }

                var element = orderElements[i];
                report.AppendLine("        " + Describe("element", element));
                var children = element?.Children;
                if (children == null)
                {
                    report.AppendLine("        children: null");
                    continue;
                }

                report.AppendLine($"        children: {children.Count}");
                for (var c = 0; c < children.Count && c < 12; c++)
                {
                    DumpChild(children[c], c, "        ", 1);
                }
            }
        });

        var path = Path.Combine(ConfigDirectory, "FaustusController_sdk-probe.txt");
        try
        {
            File.WriteAllText(path, report.ToString());
            _sdkProbeStatus = $"SDK probe: picker options={optionCount} " +
                $"(catalogue matches={resolvableCount}); orders={orderCount} " +
                $"orderElements={orderElementCount}" +
                (issues.Count > 0 ? $"; ISSUES: {string.Join("; ", issues)}" : "; no read exceptions") +
                $"; wrote {path}";
        }
        catch (Exception exception)
        {
            _sdkProbeStatus = $"SDK probe failed to write '{path}': {exception.Message}";
        }
    }
}
