namespace FaustusController;

public sealed record CurrencyScanPlanStep(
    int Index,
    CurrencyIdentity OfferedCurrency,
    CurrencyIdentity WantedCurrency)
{
    public CurrencyPairKey Pair => new(
        OfferedCurrency.Metadata,
        WantedCurrency.Metadata);
}

public sealed class CurrencyScanPlan
{
    private CurrencyScanPlan(
        IReadOnlyList<CurrencyScanPlanStep> steps,
        IReadOnlyList<CurrencyScanPlanStep> initialCollectionSteps)
    {
        Steps = steps;
        InitialCollectionSteps = initialCollectionSteps;
    }

    public IReadOnlyList<CurrencyScanPlanStep> Steps { get; }
    public IReadOnlyList<CurrencyScanPlanStep> InitialCollectionSteps { get; }

    public static bool TryCreate(
        CurrencyCatalogue catalogue,
        out CurrencyScanPlan? plan,
        out string failureReason)
    {
        if (!catalogue.TryGetUniqueByName("Chaos Orb", out var chaos))
        {
            plan = null;
            failureReason = "Could not uniquely identify Chaos Orb in the catalogue.";
            return false;
        }

        if (!catalogue.TryGetUniqueByName("Divine Orb", out var divine))
        {
            plan = null;
            failureReason = "Could not uniquely identify Divine Orb in the catalogue.";
            return false;
        }

        if (!catalogue.TryGetUniqueByName("Orb of Alteration", out var alteration))
        {
            plan = null;
            failureReason = "Could not uniquely identify Orb of Alteration in the catalogue.";
            return false;
        }

        var chaosCurrency = chaos!;
        var divineCurrency = divine!;
        var alterationCurrency = alteration!;

        var pairs = new List<(CurrencyIdentity Offered, CurrencyIdentity Wanted)>();
        var seen = new HashSet<CurrencyPairKey>();

        void Add(CurrencyIdentity offered, CurrencyIdentity wanted)
        {
            var pair = new CurrencyPairKey(offered.Metadata, wanted.Metadata);
            if (offered.Metadata != wanted.Metadata && seen.Add(pair))
            {
                pairs.Add((offered, wanted));
            }
        }

        foreach (var currency in catalogue.Items)
        {
            Add(currency, chaosCurrency);
        }

        foreach (var currency in catalogue.Items)
        {
            Add(currency, divineCurrency);
        }

        foreach (var currency in catalogue.Items)
        {
            Add(chaosCurrency, currency);
        }

        foreach (var currency in catalogue.Items)
        {
            Add(divineCurrency, currency);
        }

        var steps = pairs
            .Select((pair, index) => new CurrencyScanPlanStep(
                index,
                pair.Offered,
                pair.Wanted))
            .ToArray();
        var initialCollectionSteps = steps
            .Where(step => step.WantedCurrency.Metadata == chaosCurrency.Metadata ||
                step.WantedCurrency.Metadata == divineCurrency.Metadata)
            .OrderBy(step =>
                step.OfferedCurrency.Metadata == alterationCurrency.Metadata &&
                step.WantedCurrency.Metadata == chaosCurrency.Metadata
                    ? 0
                    : 1)
            .ThenBy(step => step.Index)
            .ToArray();
        plan = new CurrencyScanPlan(steps, initialCollectionSteps);
        failureReason = string.Empty;
        return true;
    }

    public CurrencyScanPlanStep? GetNext(CurrencyPairKey? currentPair)
    {
        return GetNext(Steps, currentPair);
    }

    public CurrencyScanPlanStep? GetNextInitialCollectionStep(CurrencyPairKey? currentPair)
    {
        return GetNext(InitialCollectionSteps, currentPair);
    }

    private static CurrencyScanPlanStep? GetNext(
        IReadOnlyList<CurrencyScanPlanStep> steps,
        CurrencyPairKey? currentPair)
    {
        if (steps.Count == 0)
        {
            return null;
        }

        if (currentPair == null)
        {
            return steps[0];
        }

        for (var index = 0; index < steps.Count; index++)
        {
            if (steps[index].Pair == currentPair)
            {
                return steps[(index + 1) % steps.Count];
            }
        }

        return steps[0];
    }
}
