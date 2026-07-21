using ExileCore;
using System.Numerics;

namespace FaustusController;

public sealed record CurrencyPickerOptionTarget(
    CurrencyIdentity Currency,
    int Owned,
    Vector2 Center,
    Vector2 Size)
{
    public bool Contains(Vector2 position, float inset = 0)
    {
        var halfSize = Size / 2;
        return position.X >= Center.X - halfSize.X + inset &&
            position.X <= Center.X + halfSize.X - inset &&
            position.Y >= Center.Y - halfSize.Y + inset &&
            position.Y <= Center.Y + halfSize.Y - inset;
    }
}

public sealed record CurrencyPickerInspection(
    bool IsPickingWantedCurrency,
    IReadOnlyList<CurrencyPickerOptionTarget> VisibleOptions);

public sealed class CurrencyPickerInspector
{
    public bool TryInspect(
        GameController gameController,
        CurrencyCatalogue catalogue,
        out CurrencyPickerInspection? inspection,
        out string failureReason)
    {
        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (!panel.IsVisible)
        {
            inspection = null;
            failureReason = "Currency Exchange panel is not visible.";
            return false;
        }

        var picker = panel.CurrencyPicker;
        if (!picker.IsVisible)
        {
            inspection = null;
            failureReason = "Open the wanted or offered currency picker to preview its target.";
            return false;
        }

        var options = new List<CurrencyPickerOptionTarget>();
        foreach (var option in picker.Options)
        {
            var item = option.ItemType;
            if (item == null || string.IsNullOrWhiteSpace(item.Metadata) || !option.IsVisible)
            {
                continue;
            }

            if (!catalogue.TryGetByMetadata(item.Metadata, out var currency))
            {
                currency = new CurrencyIdentity(item.Metadata, item.Hash, item.BaseName);
            }

            var rectangle = option.GetClientRectCache;
            if (rectangle.Width <= 0 || rectangle.Height <= 0)
            {
                continue;
            }

            options.Add(new CurrencyPickerOptionTarget(
                currency!,
                option.Owned,
                new Vector2(
                    rectangle.X + rectangle.Width / 2,
                    rectangle.Y + rectangle.Height / 2),
                new Vector2(rectangle.Width, rectangle.Height)));
        }

        inspection = new CurrencyPickerInspection(
            picker.IsPickingWantedCurrency,
            options);
        failureReason = string.Empty;
        return true;
    }
}
