using Newtonsoft.Json;

namespace FaustusController;

public readonly record struct ActiveRefreshPlanResult(
    bool Ready,
    int PairCount,
    int CanonicalActivePairCount,
    int ForceIncludedPairCount);

public sealed class ActiveRefreshPlanExporter
{
    private const int CurrentSchemaVersion = 1;

    public ActiveRefreshPlanResult Export(
        CurrencyCatalogue catalogue,
        string league,
        bool ready,
        IReadOnlyCollection<CurrencyScanPlanStep> steps,
        IReadOnlyDictionary<CurrencyPairKey, CurrencyDiscoveryProbeOutcome> outcomes,
        CurrencyDiscoveryOverrides overrides,
        string outputPath)
    {
        if (string.IsNullOrWhiteSpace(league))
        {
            throw new ArgumentException("A refresh plan requires a league.", nameof(league));
        }

        if (!catalogue.TryGetUniqueByName("Chaos Orb", out var chaos) ||
            !catalogue.TryGetUniqueByName("Divine Orb", out var divine))
        {
            throw new InvalidDataException(
                "Refresh plan requires unique Chaos Orb and Divine Orb identities.");
        }

        if (!ready && steps.Count != 0)
        {
            throw new InvalidDataException(
                "An incomplete discovery cannot publish active refresh pairs.");
        }

        var pairs = steps
            .Select(step => CreatePair(
                step,
                outcomes,
                overrides,
                chaos!.Metadata,
                divine!.Metadata))
            .OrderBy(pair => pair.OfferedCurrency.Name, StringComparer.Ordinal)
            .ThenBy(pair => pair.WantedCurrency.Name, StringComparer.Ordinal)
            .ThenBy(pair => pair.OfferedCurrency.Metadata, StringComparer.Ordinal)
            .ThenBy(pair => pair.WantedCurrency.Metadata, StringComparer.Ordinal)
            .ToList();
        var file = new ActiveRefreshPlanFile
        {
            SchemaVersion = CurrentSchemaVersion,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            League = league,
            Ready = ready,
            PairCount = pairs.Count,
            CanonicalActivePairCount = pairs.Count(pair => pair.Reason == "CanonicalActive"),
            ForceIncludedPairCount = pairs.Count(pair => pair.Reason == "ForceInclude"),
            Pairs = pairs
        };
        Validate(file);

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = outputPath + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonConvert.SerializeObject(file, Formatting.Indented));
        File.Move(temporaryPath, outputPath, overwrite: true);
        return new ActiveRefreshPlanResult(
            file.Ready,
            file.PairCount,
            file.CanonicalActivePairCount,
            file.ForceIncludedPairCount);
    }

    private static ActiveRefreshPairCapture CreatePair(
        CurrencyScanPlanStep step,
        IReadOnlyDictionary<CurrencyPairKey, CurrencyDiscoveryProbeOutcome> outcomes,
        CurrencyDiscoveryOverrides overrides,
        string chaosMetadata,
        string divineMetadata)
    {
        outcomes.TryGetValue(step.Pair, out var outcome);
        var active = outcome?.Status == CurrencyDiscoveryProbeStatus.Active;
        var forceIncluded = overrides.ForceIncludeMetadata.Contains(
                step.OfferedCurrency.Metadata) ||
            overrides.ForceIncludeMetadata.Contains(step.WantedCurrency.Metadata);
        if (!active && !forceIncluded)
        {
            throw new InvalidDataException(
                "Refresh plan contains a pair that is neither active nor force-included.");
        }

        return new ActiveRefreshPairCapture
        {
            OfferedCurrency = CreateCurrency(step.OfferedCurrency),
            WantedCurrency = CreateCurrency(step.WantedCurrency),
            Direction = GetDirection(step, chaosMetadata, divineMetadata),
            Reason = active ? "CanonicalActive" : "ForceInclude",
            LatestOutcome = outcome?.Status.ToString(),
            CaptureId = outcome?.CaptureId,
            ObservedAtUtc = outcome?.ObservedAtUtc
        };
    }

    private static string GetDirection(
        CurrencyScanPlanStep step,
        string chaosMetadata,
        string divineMetadata)
    {
        if (step.WantedCurrency.Metadata == chaosMetadata)
        {
            return "SellForChaos";
        }

        if (step.WantedCurrency.Metadata == divineMetadata)
        {
            return "SellForDivine";
        }

        if (step.OfferedCurrency.Metadata == chaosMetadata)
        {
            return "BuyWithChaos";
        }

        if (step.OfferedCurrency.Metadata == divineMetadata)
        {
            return "BuyWithDivine";
        }

        throw new InvalidDataException(
            "Refresh plan pair does not use Chaos Orb or Divine Orb as an endpoint.");
    }

    private static CurrencyCapture CreateCurrency(CurrencyIdentity currency)
    {
        return new CurrencyCapture
        {
            Metadata = currency.Metadata,
            Hash = currency.Hash,
            Name = currency.Name
        };
    }

    private static void Validate(ActiveRefreshPlanFile file)
    {
        if (file.SchemaVersion != CurrentSchemaVersion ||
            file.GeneratedAtUtc == default || string.IsNullOrWhiteSpace(file.League) ||
            file.PairCount != file.Pairs.Count || file.CanonicalActivePairCount < 0 ||
            file.ForceIncludedPairCount < 0 ||
            file.CanonicalActivePairCount + file.ForceIncludedPairCount != file.PairCount ||
            !file.Ready && file.PairCount != 0)
        {
            throw new InvalidDataException("Active refresh plan root metadata is invalid.");
        }

        var pairs = new HashSet<CurrencyPairKey>();
        foreach (var pair in file.Pairs)
        {
            var key = new CurrencyPairKey(
                pair.OfferedCurrency.Metadata,
                pair.WantedCurrency.Metadata);
            if (string.IsNullOrWhiteSpace(pair.OfferedCurrency.Name) ||
                string.IsNullOrWhiteSpace(pair.WantedCurrency.Name) ||
                pair.OfferedCurrency.Metadata == pair.WantedCurrency.Metadata ||
                !pairs.Add(key) ||
                pair.Reason is not "CanonicalActive" and not "ForceInclude" ||
                pair.Direction is not "SellForChaos" and not "BuyWithChaos" and
                    not "SellForDivine" and not "BuyWithDivine" ||
                pair.Reason == "CanonicalActive" &&
                    (pair.LatestOutcome != CurrencyDiscoveryProbeStatus.Active.ToString() ||
                        pair.CaptureId == null || pair.ObservedAtUtc == null))
            {
                throw new InvalidDataException("Active refresh plan contains an invalid pair.");
            }
        }
    }
}

public sealed class ActiveRefreshPlanFile
{
    public int SchemaVersion { get; set; }
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public string League { get; set; } = "";
    public bool Ready { get; set; }
    public int PairCount { get; set; }
    public int CanonicalActivePairCount { get; set; }
    public int ForceIncludedPairCount { get; set; }
    public List<ActiveRefreshPairCapture> Pairs { get; set; } = [];
}

public sealed class ActiveRefreshPairCapture
{
    public CurrencyCapture OfferedCurrency { get; set; } = new();
    public CurrencyCapture WantedCurrency { get; set; } = new();
    public string Direction { get; set; } = "";
    public string Reason { get; set; } = "";
    public string? LatestOutcome { get; set; }
    public Guid? CaptureId { get; set; }
    public DateTimeOffset? ObservedAtUtc { get; set; }
}
