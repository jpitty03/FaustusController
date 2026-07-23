using ExileCore;

namespace FaustusController;

public sealed class CurrencyExchangeRateCollector
{
    public bool TryCaptureCurrentPair(
        GameController gameController,
        out ExchangePairSnapshot? snapshot,
        out string failureReason)
    {
        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (!panel.IsVisible)
        {
            snapshot = null;
            failureReason = "Currency Exchange panel is not visible.";
            return false;
        }

        var serverData = gameController.Game.IngameState.ServerData;
        var league = serverData.League;
        if (string.IsNullOrWhiteSpace(league))
        {
            snapshot = null;
            failureReason = "Current league is unavailable; capture was not created.";
            return false;
        }

        var offeredItem = panel.OfferedItemType;
        var wantedItem = panel.WantedItemType;
        if (offeredItem == null || wantedItem == null ||
            string.IsNullOrWhiteSpace(offeredItem.Metadata) ||
            string.IsNullOrWhiteSpace(wantedItem.Metadata))
        {
            snapshot = null;
            failureReason = "Select both offered and wanted currencies before capturing.";
            return false;
        }

        RationalExchangeRate.TryCreate(
            panel.MarketRateGet,
            panel.MarketRateGive,
            out var marketRate);

        var wantedStock = CreateStockLevels(
            panel.WantedItemStock,
            ExchangeStockSide.WantedItem,
            stock => stock.Get,
            stock => stock.Give,
            stock => stock.ListedCount);

        var offeredStock = CreateStockLevels(
            panel.OfferedItemStock,
            ExchangeStockSide.OfferedItem,
            stock => stock.Get,
            stock => stock.Give,
            stock => stock.ListedCount);

        snapshot = new ExchangePairSnapshot(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            league,
            serverData.InstanceId,
            new CurrencyIdentity(offeredItem.Metadata, offeredItem.Hash, offeredItem.BaseName),
            new CurrencyIdentity(wantedItem.Metadata, wantedItem.Hash, wantedItem.BaseName),
            marketRate,
            wantedStock,
            offeredStock);
        failureReason = string.Empty;
        return true;
    }

    private static IReadOnlyList<ExchangeStockLevel> CreateStockLevels<TStock>(
        IEnumerable<TStock> stocks,
        ExchangeStockSide side,
        Func<TStock, int> getSelector,
        Func<TStock, int> giveSelector,
        Func<TStock, int> listedCountSelector)
    {
        var levels = new List<ExchangeStockLevel>();
        foreach (var stock in stocks)
        {
            var get = getSelector(stock);
            var give = giveSelector(stock);
            if (ExchangeStockLevel.TryCreate(
                side,
                get,
                give,
                listedCountSelector(stock),
                out var level))
            {
                levels.Add(level!);
            }
        }

        return levels;
    }
}
