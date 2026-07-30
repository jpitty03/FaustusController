using Newtonsoft.Json;

namespace FaustusController;

public enum TradableCategory
{
    DivinationCards,
    Currency,
    DeliriumOrbs,
    Scarabs,
    Fossils,
    Essences,
    Other
}

/// <summary>
/// Maps catalogue currencies onto tradable categories using a curated list
/// (tradables.json: category names keyed to item-name arrays). The list is
/// authoritative and intentionally partial - it names only the items worth
/// scanning. Anything not on the list resolves to <see cref="TradableCategory.Other"/>
/// so it can be excluded by unchecking the "Other" category, and there is no
/// metadata-path guessing that would silently re-include unlisted items.
/// </summary>
public sealed class TradableCategoryResolver
{
    private static readonly IReadOnlyDictionary<string, TradableCategory> SectionHeaders =
        new Dictionary<string, TradableCategory>(StringComparer.OrdinalIgnoreCase)
        {
            ["Divination Cards"] = TradableCategory.DivinationCards,
            ["Currency"] = TradableCategory.Currency,
            ["Delirium Orbs"] = TradableCategory.DeliriumOrbs,
            ["Scarabs"] = TradableCategory.Scarabs,
            ["Fossils"] = TradableCategory.Fossils,
            ["Essences"] = TradableCategory.Essences
        };

    private readonly IReadOnlyDictionary<string, TradableCategory> _byName;

    private TradableCategoryResolver(
        IReadOnlyDictionary<string, TradableCategory> byName)
    {
        _byName = byName;
    }

    public static TradableCategoryResolver Empty { get; } = new(
        new Dictionary<string, TradableCategory>(StringComparer.OrdinalIgnoreCase));

    public int NamedEntryCount => _byName.Count;

    /// <summary>Count of named entries in one category, for status reporting.</summary>
    public int CountFor(TradableCategory category)
    {
        return _byName.Count(entry => entry.Value == category);
    }

    public static TradableCategoryResolver Load(string path)
    {
        var byName = new Dictionary<string, TradableCategory>(
            StringComparer.OrdinalIgnoreCase);
        var sections = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? ReadJsonSections(path)
            : ReadTextSections(path);
        foreach (var (sectionName, itemNames) in sections)
        {
            if (!SectionHeaders.TryGetValue(sectionName, out var category))
            {
                throw new InvalidDataException(
                    $"Unknown tradables category \"{sectionName}\"; expected one of: " +
                    string.Join(", ", SectionHeaders.Keys));
            }

            foreach (var itemName in itemNames)
            {
                var trimmed = itemName.Trim();
                if (trimmed.Length > 0)
                {
                    byName[trimmed] = category;
                }
            }
        }

        return new TradableCategoryResolver(byName);
    }

    private static IEnumerable<KeyValuePair<string, List<string>>> ReadJsonSections(string path)
    {
        return JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(
            File.ReadAllText(path)) ?? throw new InvalidDataException(
            $"tradables.json deserialised to null: {path}");
    }

    // Legacy text format: a section header line, then one item name per line,
    // blank lines ignored. Kept so an existing tradables.txt still works.
    private static IEnumerable<KeyValuePair<string, List<string>>> ReadTextSections(string path)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        List<string>? current = null;
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (SectionHeaders.ContainsKey(line))
            {
                if (!sections.TryGetValue(line, out current))
                {
                    current = [];
                    sections[line] = current;
                }

                continue;
            }

            current?.Add(line);
        }

        return sections;
    }

    public TradableCategory Resolve(CurrencyIdentity currency)
    {
        return _byName.TryGetValue(currency.Name, out var category)
            ? category
            : TradableCategory.Other;
    }
}
