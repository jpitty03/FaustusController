using ExileCore;
using ExileCore.PoEMemory;
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
        out string failureReason,
        bool includeOffScreenOptions = false)
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

        var container = picker.OptionContainer;
        var containerRectangle = container != null ? container.GetClientRectCache : default;
        var hasContainerRectangle = containerRectangle.Width > 0 && containerRectangle.Height > 0;

        var options = new List<CurrencyPickerOptionTarget>();
        foreach (var option in picker.Options)
        {
            if (option == null || !option.IsVisible)
            {
                continue;
            }

            var rectangle = option.GetClientRectCache;
            if (rectangle.Width <= 0 || rectangle.Height <= 0)
            {
                continue;
            }

            // Skip tiles scrolled outside the option container so the text
            // fallback below only reads on-screen elements. One-shot flows
            // (inventory sync) opt out so they can read the entire list.
            if (!includeOffScreenOptions &&
                hasContainerRectangle &&
                (rectangle.Bottom < containerRectangle.Top ||
                 rectangle.Top > containerRectangle.Bottom))
            {
                continue;
            }

            var currency = ResolveIdentity(option, catalogue);
            if (currency == null)
            {
                continue;
            }

            options.Add(new CurrencyPickerOptionTarget(
                currency,
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

    private static CurrencyIdentity? ResolveIdentity(
        ExileCore.PoEMemory.Elements.Village.CurrencyExchangeCurrencyPickerCurrencyOption option,
        CurrencyCatalogue catalogue)
    {
        ExileCore.PoEMemory.Models.BaseItemType? item = null;
        try
        {
            item = option.ItemType;
        }
        catch
        {
            // Stale SDK offsets can throw on this read; fall through to text matching.
        }

        if (item != null && !string.IsNullOrWhiteSpace(item.Metadata))
        {
            if (!catalogue.TryGetByMetadata(item.Metadata, out var currency))
            {
                currency = new CurrencyIdentity(item.Metadata, item.Hash, item.BaseName);
            }

            return currency;
        }

        // Fallback for SDK builds whose option ItemType offset is stale (e.g. after
        // a game patch): the option tile still renders the currency name as text,
        // so match that text against the exchange catalogue by name.
        return TryResolveByText(option, catalogue);
    }

    public static CurrencyIdentity? TryResolveByText(Element option, CurrencyCatalogue catalogue)
    {
        foreach (var text in EnumerateTexts(option, 0))
        {
            if (catalogue.TryGetUniqueByName(text, out var currency))
            {
                return currency;
            }
        }

        return null;
    }

    public static IEnumerable<string> EnumerateTexts(Element? element, int depth)
    {
        if (element == null || depth > 4)
        {
            yield break;
        }

        string? text = null;
        try
        {
            text = element.TextNoTags;
            if (string.IsNullOrWhiteSpace(text))
            {
                text = element.Text;
            }
        }
        catch
        {
            // Ignore unreadable text elements.
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            var trimmed = text.Trim();
            if (trimmed.Length >= 2 && !trimmed.All(char.IsDigit))
            {
                yield return trimmed;
            }
        }

        IList<Element>? children = null;
        try
        {
            children = element.Children;
        }
        catch
        {
            // Ignore unreadable children.
        }

        if (children == null)
        {
            yield break;
        }

        foreach (var child in children)
        {
            foreach (var childText in EnumerateTexts(child, depth + 1))
            {
                yield return childText;
            }
        }
    }
}
