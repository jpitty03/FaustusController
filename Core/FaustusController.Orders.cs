using ExileCore.PoEMemory.Models;
using Newtonsoft.Json;

namespace FaustusController;

public sealed partial class FaustusController
{
    private const int PlacedOrdersSchemaVersion = 1;

    private string _inventorySyncStatus =
        "Open a currency picker and press NumPad7 to sync inventory balances.";
    private string _placedOrdersStatus =
        "Press NumPad8 to export placed orders; NumPad9 to calibrate gold cost.";
    private string _placedOrdersPath = "";

    private void SyncInventoryFromPicker()
    {
        if (IsAnyAutomationRunning)
        {
            _inventorySyncStatus = "Inventory sync blocked: automated scan is running.";
            return;
        }

        if (_catalogue == null)
        {
            _inventorySyncStatus = "Inventory sync blocked: the live catalogue is unavailable.";
            return;
        }

        var league = GetCurrentLeague();
        if (string.IsNullOrWhiteSpace(league))
        {
            _inventorySyncStatus = "Inventory sync blocked: the current league is unavailable.";
            return;
        }

        if (!_pickerInspector.TryInspect(
            GameController,
            _catalogue,
            out var inspection,
            out var failureReason))
        {
            _inventorySyncStatus = $"Inventory sync blocked: {failureReason}";
            return;
        }

        try
        {
            var requestPath = Path.Combine(
                ConfigDirectory,
                $"FaustusController_route-request-{SanitizeFileName(league)}.json");
            var request = LoadRouteRequestForEdit(league, requestPath);

            // The analyzer rejects balances for the target currency, so never
            // store one and silently drop invalid or duplicate manual entries.
            var targetMetadata = request.TargetCurrency.Metadata;
            var balances = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var balance in request.InventoryBalances)
            {
                if (string.IsNullOrWhiteSpace(balance.Metadata) ||
                    balance.Units <= 0 ||
                    balance.Metadata == targetMetadata)
                {
                    continue;
                }

                balances[balance.Metadata] = balance.Units;
            }

            var seen = 0;
            var updated = 0;
            var removed = 0;
            var targetSkipped = false;
            foreach (var option in inspection!.VisibleOptions)
            {
                seen++;
                if (option.Currency.Metadata == targetMetadata)
                {
                    targetSkipped = true;
                    continue;
                }

                if (option.Owned > 0)
                {
                    if (!balances.TryGetValue(option.Currency.Metadata, out var existing) ||
                        existing != option.Owned)
                    {
                        updated++;
                    }

                    balances[option.Currency.Metadata] = option.Owned;
                }
                else if (balances.Remove(option.Currency.Metadata))
                {
                    removed++;
                }
            }

            request.InventoryBalances = balances
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new CurrencyBalance(entry.Key, entry.Value))
                .ToList();
            WriteOrdersJsonAtomically(requestPath, request);
            _inventorySyncStatus =
                $"Inventory sync: {seen} visible options seen; {updated} updated, " +
                $"{removed} removed; {request.InventoryBalances.Count} balances stored" +
                (targetSkipped ? $"; skipped target {request.TargetCurrency.Name}. " : ". ") +
                "Press Home to re-run analysis.";
        }
        catch (Exception exception)
        {
            _inventorySyncStatus = $"Inventory sync failed: {exception.Message}";
        }
    }

    private void ExportPlacedOrders()
    {
        var league = GetCurrentLeague();
        if (string.IsNullOrWhiteSpace(league))
        {
            _placedOrdersStatus = "Placed orders blocked: the current league is unavailable.";
            return;
        }

        if (!TryCollectPlacedOrders(out var orders, out var failure))
        {
            _placedOrdersStatus = $"Placed orders blocked: {failure}";
            return;
        }

        try
        {
            var pending = orders.Count(order => order.Status == "Pending");
            var completed = orders.Count(order => order.Status == "Completed");
            var canceled = orders.Count(order => order.Status == "Canceled");
            var goldCosts = orders
                .Where(order => order.GoldCost > 0)
                .Select(order => (long)order.GoldCost)
                .ToList();
            var file = new PlacedOrdersFile
            {
                SchemaVersion = PlacedOrdersSchemaVersion,
                CapturedAtUtc = DateTimeOffset.UtcNow,
                League = league,
                PendingCount = pending,
                CompletedCount = completed,
                CanceledCount = canceled,
                TotalGoldCost = goldCosts.Sum(),
                MedianGoldCostPerOrder = ComputeMedian(goldCosts),
                Orders = orders
            };
            _placedOrdersPath = Path.Combine(
                ConfigDirectory,
                $"FaustusController_placed-orders-{SanitizeFileName(league)}.json");
            WriteOrdersJsonAtomically(_placedOrdersPath, file);
            _placedOrdersStatus =
                $"Placed orders: {orders.Count} exported " +
                $"({pending} pending, {completed} completed, {canceled} canceled); " +
                $"total gold {file.TotalGoldCost}, median {file.MedianGoldCostPerOrder}.";
        }
        catch (Exception exception)
        {
            _placedOrdersStatus = $"Placed orders export failed: {exception.Message}";
        }
    }

    private void CalibrateGoldCostFromOrders()
    {
        if (IsAnyAutomationRunning)
        {
            _placedOrdersStatus = "Gold calibration blocked: automated scan is running.";
            return;
        }

        if (_catalogue == null)
        {
            _placedOrdersStatus = "Gold calibration blocked: the live catalogue is unavailable.";
            return;
        }

        var league = GetCurrentLeague();
        if (string.IsNullOrWhiteSpace(league))
        {
            _placedOrdersStatus = "Gold calibration blocked: the current league is unavailable.";
            return;
        }

        if (!TryCollectPlacedOrders(out var orders, out var failure))
        {
            _placedOrdersStatus = $"Gold calibration blocked: {failure}";
            return;
        }

        var goldCosts = orders
            .Where(order => order.GoldCost > 0)
            .Select(order => (long)order.GoldCost)
            .ToList();
        if (goldCosts.Count == 0)
        {
            _placedOrdersStatus =
                "Gold calibration blocked: no placed orders with a gold cost were found.";
            return;
        }

        try
        {
            var requestPath = Path.Combine(
                ConfigDirectory,
                $"FaustusController_route-request-{SanitizeFileName(league)}.json");
            var request = LoadRouteRequestForEdit(league, requestPath);
            var previous = request.GoldCostPerHop;
            var median = ComputeMedian(goldCosts);
            request.GoldCostPerHop = median;
            WriteOrdersJsonAtomically(requestPath, request);
            _placedOrdersStatus =
                $"Gold calibration: GoldCostPerHop {previous} -> {median} " +
                $"(median of {goldCosts.Count} order(s), " +
                $"min {goldCosts.Min()}, max {goldCosts.Max()}). " +
                "Press Home to re-run analysis.";
        }
        catch (Exception exception)
        {
            _placedOrdersStatus = $"Gold calibration failed: {exception.Message}";
        }
    }

    private CurrencyRouteRequestFile LoadRouteRequestForEdit(string league, string requestPath)
    {
        if (!File.Exists(requestPath))
        {
            return _routeAnalyzer.LoadOrCreateRequest(_catalogue!, league, requestPath);
        }

        // Load without strict validation so hotkey edits can repair a request
        // file the analyzer would reject (for example a manually added balance
        // for the target currency). Home still validates before analysis runs.
        return JsonConvert.DeserializeObject<CurrencyRouteRequestFile>(
            File.ReadAllText(requestPath)) ??
            throw new InvalidDataException("The route request file is empty.");
    }

    private bool TryCollectPlacedOrders(
        out List<PlacedOrderCapture> orders,
        out string failure)
    {
        orders = [];
        var panel = GameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (!panel.IsVisible)
        {
            failure = "Currency Exchange panel is not visible.";
            return false;
        }

        var placedOrders = panel.Orders;
        if (placedOrders == null)
        {
            failure = "Placed orders are unavailable on the panel.";
            return false;
        }

        foreach (var order in placedOrders)
        {
            if (order == null)
            {
                continue;
            }

            orders.Add(new PlacedOrderCapture
            {
                PlayerOrderId = order.PlayerOrderId,
                CreationDate = order.CreationDate,
                Status = order.IsCanceled
                    ? "Canceled"
                    : order.IsCompleted
                        ? "Completed"
                        : "Pending",
                OfferedCurrency = CreateOrderCurrency(order.OfferedItemType),
                WantedCurrency = CreateOrderCurrency(order.WantedItemType),
                OriginalOfferedStackSize = order.OriginalOfferedItemStackSize,
                RemainingOfferedStackSize = order.OfferedItemStackSize,
                WantedStackSize = order.WantedItemStackSize,
                GoldCost = order.GoldCost,
                OfferedRatioPart = order.OfferedItemRatioPart,
                WantedRatioPart = order.WantedItemRatioPart,
                CompetingOfferedRatioPart = order.CompetingOfferedItemRatioPart,
                CompetingWantedRatioPart = order.CompetingWantedItemRatioPart
            });
        }

        failure = "";
        return true;
    }

    private CurrencyCapture CreateOrderCurrency(BaseItemType? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Metadata))
        {
            return new CurrencyCapture();
        }

        if (_catalogue != null &&
            _catalogue.TryGetByMetadata(item.Metadata, out var identity) &&
            identity != null)
        {
            return new CurrencyCapture
            {
                Metadata = identity.Metadata,
                Hash = identity.Hash,
                Name = identity.Name
            };
        }

        return new CurrencyCapture
        {
            Metadata = item.Metadata,
            Hash = item.Hash,
            Name = item.BaseName
        };
    }

    private static long ComputeMedian(List<long> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.OrderBy(value => value).ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private static void WriteOrdersJsonAtomically(string path, object value)
    {
        var temporaryPath = path + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonConvert.SerializeObject(value, Formatting.Indented));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private void StartOrderAmountInput(bool wantedInput)
    {
        if (!Settings.AllowOrderAmountInput)
        {
            _amountInputController.Cancel(
                "Amount input blocked: enable Allow Order Amount Input first.");
            return;
        }

        if (IsAnyAutomationRunning)
        {
            _amountInputController.Cancel(
                "Amount input blocked: automated scanning is running.");
            return;
        }

        if (_pickerOpenController.IsRunning || _searchQueryController.IsRunning ||
            _cursorTweenController.IsRunning || _selectionController.IsRunning)
        {
            _amountInputController.Cancel(
                "Amount input blocked: another input operation is running.");
            return;
        }

        var analysis = _lastRouteAnalysis;
        if (analysis == null || analysis.Routes.Count == 0)
        {
            _amountInputController.Cancel(
                "Amount input blocked: run route analysis (Home) first.");
            return;
        }

        var routeIndex = Math.Clamp(_routeDisplayIndex, 0, analysis.Routes.Count - 1);
        var route = analysis.Routes[routeIndex];
        if (route.Hops.Count == 0)
        {
            _amountInputController.Cancel(
                "Amount input blocked: the selected route has no hops.");
            return;
        }

        var hop = route.Hops[0];
        var amount = wantedInput ? hop.Received : hop.Spent;
        var triggerKey = wantedInput
            ? Settings.TypeWantedAmount.Value.Key
            : Settings.TypeOfferedAmount.Value.Key;
        _amountInputController.Start(
            GameController,
            wantedInput,
            amount,
            triggerKey,
            Settings.CursorTweenSpeed.Value,
            out _);
    }
}
