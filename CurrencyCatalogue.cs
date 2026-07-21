using ExileCore;

namespace FaustusController;

public sealed class CurrencyCatalogue
{
    private readonly Dictionary<string, CurrencyIdentity> _byMetadata;

    public CurrencyCatalogue(IEnumerable<CurrencyIdentity> currencies)
    {
        Items = currencies
            .GroupBy(currency => currency.Metadata, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(currency => currency.Name, StringComparer.Ordinal)
            .ThenBy(currency => currency.Metadata, StringComparer.Ordinal)
            .ToArray();
        _byMetadata = Items.ToDictionary(
            currency => currency.Metadata,
            StringComparer.Ordinal);
    }

    public IReadOnlyList<CurrencyIdentity> Items { get; }

    public bool TryGetByMetadata(string metadata, out CurrencyIdentity? currency)
    {
        return _byMetadata.TryGetValue(metadata, out currency);
    }

    public bool TryGetUniqueByName(string name, out CurrencyIdentity? currency)
    {
        var matches = Items
            .Where(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        currency = matches.Length == 1 ? matches[0] : null;
        return currency != null;
    }
}

public sealed class CurrencyCatalogueBuilder
{
    public bool TryBuild(
        GameController gameController,
        out CurrencyCatalogue? catalogue,
        out string failureReason)
    {
        var entries = gameController.Files.CurrencyExchange.EntriesList;
        if (entries.Count == 0)
        {
            catalogue = null;
            failureReason = "Currency Exchange catalogue is not loaded yet.";
            return false;
        }

        var currencies = entries
            .Select(entry => entry.BaseItemType)
            .Where(item => item != null && !string.IsNullOrWhiteSpace(item.Metadata))
            .Select(item => new CurrencyIdentity(item.Metadata, item.Hash, item.BaseName))
            .ToArray();
        catalogue = new CurrencyCatalogue(currencies);
        if (catalogue.Items.Count == 0)
        {
            catalogue = null;
            failureReason = "Currency Exchange catalogue contains no valid currencies.";
            return false;
        }

        failureReason = string.Empty;
        return true;
    }
}
