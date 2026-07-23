using Newtonsoft.Json;

namespace FaustusController;

public sealed record CurrencyDiscoveryOverrides(
    IReadOnlySet<string> ForceIncludeMetadata,
    IReadOnlySet<string> ForceSkipMetadata)
{
    public static CurrencyDiscoveryOverrides Empty { get; } = new(
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal));
}

public sealed class CurrencyDiscoveryOverrideStore
{
    private const int CurrentSchemaVersion = 1;

    public CurrencyDiscoveryOverrides LoadOrCreate(
        string path,
        string league,
        CurrencyCatalogue catalogue)
    {
        if (!File.Exists(path))
        {
            SaveEmpty(path, league);
        }

        var file = JsonConvert.DeserializeObject<CurrencyDiscoveryOverrideFile>(
            File.ReadAllText(path)) ??
            throw new InvalidDataException("The discovery override file is empty.");
        if (file.SchemaVersion != CurrentSchemaVersion ||
            !string.Equals(file.League, league, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The discovery override schema or league is invalid.");
        }

        var included = ValidateEntries(file.ForceInclude, catalogue, "ForceInclude");
        var skipped = ValidateEntries(file.ForceSkip, catalogue, "ForceSkip");
        if (included.Overlaps(skipped))
        {
            throw new InvalidDataException(
                "A currency cannot appear in both ForceInclude and ForceSkip.");
        }

        foreach (var baseName in new[] { "Chaos Orb", "Divine Orb" })
        {
            if (!catalogue.TryGetUniqueByName(baseName, out var currency))
            {
                throw new InvalidDataException($"Could not resolve {baseName} for overrides.");
            }

            if (included.Contains(currency!.Metadata) || skipped.Contains(currency.Metadata))
            {
                throw new InvalidDataException(
                    $"{baseName} cannot be manually included or skipped.");
            }
        }

        return new CurrencyDiscoveryOverrides(included, skipped);
    }

    private static HashSet<string> ValidateEntries(
        IReadOnlyCollection<CurrencyDiscoveryOverrideEntry> entries,
        CurrencyCatalogue catalogue,
        string fieldName)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Metadata) ||
                !catalogue.TryGetByMetadata(entry.Metadata, out _) ||
                !result.Add(entry.Metadata))
            {
                throw new InvalidDataException(
                    $"{fieldName} contains an unknown or duplicate metadata identity.");
            }
        }

        return result;
    }

    private static void SaveEmpty(string path, string league)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var file = new CurrencyDiscoveryOverrideFile
        {
            SchemaVersion = CurrentSchemaVersion,
            League = league
        };
        var temporaryPath = path + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonConvert.SerializeObject(file, Formatting.Indented));
        File.Move(temporaryPath, path, overwrite: true);
    }
}

public sealed class CurrencyDiscoveryOverrideFile
{
    public int SchemaVersion { get; set; }
    public string League { get; set; } = "";
    public List<CurrencyDiscoveryOverrideEntry> ForceInclude { get; set; } = [];
    public List<CurrencyDiscoveryOverrideEntry> ForceSkip { get; set; } = [];
}

public sealed class CurrencyDiscoveryOverrideEntry
{
    public string Metadata { get; set; } = "";
    public string Name { get; set; } = "";
}
