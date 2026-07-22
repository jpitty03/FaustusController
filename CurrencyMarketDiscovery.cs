using Newtonsoft.Json;

namespace FaustusController;

public readonly record struct ActiveMarketDiscoveryResult(
    int CatalogueCurrencyCount,
    long EligibleProbePairCount,
    int ObservedActivePairCount,
    int ActiveCurrencyCount);

public sealed class ActiveMarketDiscoveryExporter
{
    private const int CurrentSchemaVersion = 1;

    private enum MarketDirection
    {
        SellForChaos,
        SellForDivine,
        BuyWithChaos,
        BuyWithDivine
    }

    public ActiveMarketDiscoveryResult Export(
        CurrencyCatalogue catalogue,
        IReadOnlyCollection<ExchangePairSnapshot> snapshots,
        string league,
        TimeSpan maximumQuoteAge,
        string outputPath,
        IReadOnlyCollection<CurrencyDiscoveryProbeOutcome>? probeOutcomes = null,
        IReadOnlySet<string>? skippedCurrencyMetadata = null,
        IReadOnlySet<string>? includedCurrencyMetadata = null)
    {
        if (string.IsNullOrWhiteSpace(league))
        {
            throw new ArgumentException("A current league is required.", nameof(league));
        }

        if (!catalogue.TryGetUniqueByName("Chaos Orb", out var chaos) ||
            !catalogue.TryGetUniqueByName("Divine Orb", out var divine))
        {
            throw new InvalidDataException(
                "Chaos Orb and Divine Orb must be uniquely present in the currency catalogue.");
        }

        var chaosCurrency = chaos!;
        var divineCurrency = divine!;
        var phaseOnePairs = catalogue.Items
            .Where(currency => skippedCurrencyMetadata == null ||
                !skippedCurrencyMetadata.Contains(currency.Metadata))
            .SelectMany(currency => new[]
            {
                new CurrencyPairKey(currency.Metadata, chaosCurrency.Metadata),
                new CurrencyPairKey(currency.Metadata, divineCurrency.Metadata)
            })
            .Where(pair => pair.OfferedMetadata != pair.WantedMetadata)
            .ToHashSet();
        var outcomesByPair = (probeOutcomes ?? [])
            .Where(outcome => string.Equals(outcome.League, league, StringComparison.Ordinal))
            .ToDictionary(outcome => outcome.Pair);
        var phaseOneComplete = phaseOnePairs.All(pair =>
            outcomesByPair.TryGetValue(pair, out var outcome) &&
            outcome.Status is CurrencyDiscoveryProbeStatus.Active or
                CurrencyDiscoveryProbeStatus.NoMarketRate or
                CurrencyDiscoveryProbeStatus.Unavailable);
        var eligibleReversePairs = phaseOneComplete
            ? phaseOnePairs
                .Where(pair =>
                    outcomesByPair.TryGetValue(pair, out var outcome) &&
                        outcome.Status == CurrencyDiscoveryProbeStatus.Active ||
                    includedCurrencyMetadata != null &&
                        includedCurrencyMetadata.Contains(pair.OfferedMetadata) ||
                    outcomesByPair.TryGetValue(
                        new CurrencyPairKey(
                            pair.WantedMetadata,
                            pair.OfferedMetadata),
                        out var reverseOutcome) &&
                        reverseOutcome.Status == CurrencyDiscoveryProbeStatus.Active)
                .Select(pair => new CurrencyPairKey(
                    pair.WantedMetadata,
                    pair.OfferedMetadata))
                .Where(pair => !phaseOnePairs.Contains(pair))
                .ToHashSet()
            : [];

        var generatedAtUtc = DateTimeOffset.UtcNow;
        var oldestFreshCapture = generatedAtUtc - maximumQuoteAge;
        var negativeByPair = (probeOutcomes ?? [])
            .Where(outcome =>
                (outcome.Status is CurrencyDiscoveryProbeStatus.NoMarketRate or
                    CurrencyDiscoveryProbeStatus.Unavailable) &&
                string.Equals(outcome.League, league, StringComparison.Ordinal))
            .GroupBy(outcome => outcome.Pair)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(outcome => outcome.ObservedAtUtc).First());
        var catalogueByMetadata = catalogue.Items.ToDictionary(
            currency => currency.Metadata,
            StringComparer.Ordinal);
        var existing = LoadExisting(outputPath, league);
        var currenciesByMetadata = existing.Currencies
            .Where(currency =>
                catalogueByMetadata.ContainsKey(currency.Currency.Metadata))
            .ToDictionary(
                currency => currency.Currency.Metadata,
                StringComparer.Ordinal);
        foreach (var (metadata, currency) in currenciesByMetadata)
        {
            currency.Currency = CreateCurrency(catalogueByMetadata[metadata]);
            if (metadata == chaosCurrency.Metadata)
            {
                currency.ChaosMarket = null;
            }

            if (metadata == divineCurrency.Metadata)
            {
                currency.DivineMarket = null;
            }

            if (!eligibleReversePairs.Contains(new CurrencyPairKey(
                chaosCurrency.Metadata,
                metadata)))
            {
                currency.ChaosBuyMarket = null;
            }

            if (!eligibleReversePairs.Contains(new CurrencyPairKey(
                divineCurrency.Metadata,
                metadata)))
            {
                currency.DivineBuyMarket = null;
            }

            RefreshAge(currency.ChaosMarket, generatedAtUtc, oldestFreshCapture);
            RefreshAge(currency.DivineMarket, generatedAtUtc, oldestFreshCapture);
            RefreshAge(currency.ChaosBuyMarket, generatedAtUtc, oldestFreshCapture);
            RefreshAge(currency.DivineBuyMarket, generatedAtUtc, oldestFreshCapture);
        }

        foreach (var outcome in probeOutcomes ?? [])
        {
            if (outcome.Status is not CurrencyDiscoveryProbeStatus.NoMarketRate and
                    not CurrencyDiscoveryProbeStatus.Unavailable ||
                !string.Equals(outcome.League, league, StringComparison.Ordinal) ||
                !catalogueByMetadata.ContainsKey(outcome.OfferedCurrency.Metadata) ||
                !catalogueByMetadata.ContainsKey(outcome.WantedCurrency.Metadata) ||
                outcome.Pair.OfferedMetadata == outcome.Pair.WantedMetadata ||
                !TryResolveDirection(
                    outcome.Pair,
                    chaosCurrency.Metadata,
                    divineCurrency.Metadata,
                    eligibleReversePairs,
                    out var currencyMetadata,
                    out var direction) ||
                !currenciesByMetadata.TryGetValue(
                    currencyMetadata,
                    out var currency))
            {
                continue;
            }

            ClearMarketIfOlder(currency, direction, outcome.ObservedAtUtc);
        }

        var activePairs = snapshots
            .Where(snapshot => string.Equals(snapshot.League, league, StringComparison.Ordinal) &&
                snapshot.MarketRate != null &&
                catalogueByMetadata.ContainsKey(snapshot.OfferedCurrency.Metadata) &&
                catalogueByMetadata.ContainsKey(snapshot.WantedCurrency.Metadata) &&
                snapshot.OfferedCurrency.Metadata != snapshot.WantedCurrency.Metadata &&
                TryResolveDirection(
                    snapshot.Pair,
                    chaosCurrency.Metadata,
                    divineCurrency.Metadata,
                    eligibleReversePairs,
                    out _,
                    out _) &&
                (!negativeByPair.TryGetValue(snapshot.Pair, out var negative) ||
                    negative.ObservedAtUtc < snapshot.CapturedAtUtc))
            .GroupBy(snapshot => snapshot.Pair)
            .Select(group => group
                .OrderByDescending(snapshot => snapshot.CapturedAtUtc)
                .ThenByDescending(snapshot => snapshot.CaptureId)
                .First())
            .ToArray();

        foreach (var snapshot in activePairs)
        {
            _ = TryResolveDirection(
                snapshot.Pair,
                chaosCurrency.Metadata,
                divineCurrency.Metadata,
                eligibleReversePairs,
                out var currencyMetadata,
                out var direction);
            if (!currenciesByMetadata.TryGetValue(
                currencyMetadata,
                out var currency))
            {
                currency = new ActiveCurrencyMarketCapture
                {
                    Currency = CreateCurrency(catalogueByMetadata[currencyMetadata])
                };
                currenciesByMetadata[currencyMetadata] = currency;
            }

            var market = CreateMarket(snapshot, generatedAtUtc, oldestFreshCapture)!;
            SetMarketIfNewer(currency, direction, market);
        }

        var currencies = currenciesByMetadata.Values
            .Where(currency => currency.ChaosMarket != null ||
                currency.DivineMarket != null ||
                currency.ChaosBuyMarket != null ||
                currency.DivineBuyMarket != null)
            .Where(currency => skippedCurrencyMetadata == null ||
                !skippedCurrencyMetadata.Contains(currency.Currency.Metadata))
            .ToList();
        foreach (var currency in currencies)
        {
            var chaosObserved = currency.ChaosMarket != null || currency.ChaosBuyMarket != null;
            var divineObserved = currency.DivineMarket != null || currency.DivineBuyMarket != null;
            currency.ObservedMarkets = chaosObserved && divineObserved
                ? "ChaosAndDivineObserved"
                : chaosObserved
                    ? "ChaosObserved"
                    : "DivineObserved";
            currency.ObservedDirections = GetObservedDirections(currency);
        }

        var observedActivePairCount = currencies.Sum(currency =>
            (currency.ChaosMarket == null ? 0 : 1) +
            (currency.DivineMarket == null ? 0 : 1) +
            (currency.ChaosBuyMarket == null ? 0 : 1) +
            (currency.DivineBuyMarket == null ? 0 : 1));

        var export = new ActiveMarketDiscoveryFile
        {
            SchemaVersion = CurrentSchemaVersion,
            GeneratedAtUtc = generatedAtUtc,
            League = league,
            MaximumQuoteAgeMinutes = (int)Math.Ceiling(maximumQuoteAge.TotalMinutes),
            CatalogueCurrencyCount = catalogue.Items.Count,
            EligibleProbePairCount = phaseOnePairs.Count + eligibleReversePairs.Count,
            ObservedActivePairCount = observedActivePairCount,
            ActiveCurrencyCount = currencies.Count,
            Currencies = currencies
                .OrderBy(currency => currency.Currency.Name, StringComparer.Ordinal)
                .ThenBy(currency => currency.Currency.Metadata, StringComparer.Ordinal)
                .ToList()
        };

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = outputPath + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonConvert.SerializeObject(export, Formatting.Indented));
        File.Move(temporaryPath, outputPath, overwrite: true);
        return new ActiveMarketDiscoveryResult(
            export.CatalogueCurrencyCount,
            export.EligibleProbePairCount,
            export.ObservedActivePairCount,
            export.ActiveCurrencyCount);
    }

    private static bool TryResolveDirection(
        CurrencyPairKey pair,
        string chaosMetadata,
        string divineMetadata,
        IReadOnlySet<CurrencyPairKey> eligibleReversePairs,
        out string currencyMetadata,
        out MarketDirection direction)
    {
        if (pair.WantedMetadata == chaosMetadata)
        {
            currencyMetadata = pair.OfferedMetadata;
            direction = MarketDirection.SellForChaos;
            return true;
        }

        if (pair.WantedMetadata == divineMetadata)
        {
            currencyMetadata = pair.OfferedMetadata;
            direction = MarketDirection.SellForDivine;
            return true;
        }

        if (eligibleReversePairs.Contains(pair) && pair.OfferedMetadata == chaosMetadata)
        {
            currencyMetadata = pair.WantedMetadata;
            direction = MarketDirection.BuyWithChaos;
            return true;
        }

        if (eligibleReversePairs.Contains(pair) && pair.OfferedMetadata == divineMetadata)
        {
            currencyMetadata = pair.WantedMetadata;
            direction = MarketDirection.BuyWithDivine;
            return true;
        }

        currencyMetadata = "";
        direction = default;
        return false;
    }

    private static void ClearMarketIfOlder(
        ActiveCurrencyMarketCapture currency,
        MarketDirection direction,
        DateTimeOffset negativeObservedAtUtc)
    {
        var market = GetMarket(currency, direction);
        if (market == null || market.CapturedAtUtc > negativeObservedAtUtc)
        {
            return;
        }

        SetMarket(currency, direction, null);
    }

    private static void SetMarketIfNewer(
        ActiveCurrencyMarketCapture currency,
        MarketDirection direction,
        ActiveMarketQuoteCapture market)
    {
        if (IsNewer(market, GetMarket(currency, direction)))
        {
            SetMarket(currency, direction, market);
        }
    }

    private static ActiveMarketQuoteCapture? GetMarket(
        ActiveCurrencyMarketCapture currency,
        MarketDirection direction)
    {
        return direction switch
        {
            MarketDirection.SellForChaos => currency.ChaosMarket,
            MarketDirection.SellForDivine => currency.DivineMarket,
            MarketDirection.BuyWithChaos => currency.ChaosBuyMarket,
            MarketDirection.BuyWithDivine => currency.DivineBuyMarket,
            _ => null
        };
    }

    private static void SetMarket(
        ActiveCurrencyMarketCapture currency,
        MarketDirection direction,
        ActiveMarketQuoteCapture? market)
    {
        switch (direction)
        {
            case MarketDirection.SellForChaos:
                currency.ChaosMarket = market;
                return;
            case MarketDirection.SellForDivine:
                currency.DivineMarket = market;
                return;
            case MarketDirection.BuyWithChaos:
                currency.ChaosBuyMarket = market;
                return;
            case MarketDirection.BuyWithDivine:
                currency.DivineBuyMarket = market;
                return;
        }
    }

    private static List<string> GetObservedDirections(ActiveCurrencyMarketCapture currency)
    {
        var result = new List<string>();
        if (currency.ChaosMarket != null)
        {
            result.Add("SellForChaos");
        }

        if (currency.ChaosBuyMarket != null)
        {
            result.Add("BuyWithChaos");
        }

        if (currency.DivineMarket != null)
        {
            result.Add("SellForDivine");
        }

        if (currency.DivineBuyMarket != null)
        {
            result.Add("BuyWithDivine");
        }

        return result;
    }

    private static ActiveMarketDiscoveryFile LoadExisting(
        string outputPath,
        string league)
    {
        if (!File.Exists(outputPath))
        {
            return new ActiveMarketDiscoveryFile { League = league };
        }

        var existing = JsonConvert.DeserializeObject<ActiveMarketDiscoveryFile>(
            File.ReadAllText(outputPath)) ??
            throw new InvalidDataException("The active-market discovery file is empty.");
        if (existing.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported active-market schema version {existing.SchemaVersion}.");
        }

        foreach (var currency in existing.Currencies)
        {
            if (string.IsNullOrWhiteSpace(currency.Currency.Metadata) ||
                string.IsNullOrWhiteSpace(currency.Currency.Name))
            {
                throw new InvalidDataException(
                    "An active-market currency has an invalid identity.");
            }

            ValidateMarket(currency.ChaosMarket);
            ValidateMarket(currency.DivineMarket);
            ValidateMarket(currency.ChaosBuyMarket);
            ValidateMarket(currency.DivineBuyMarket);
        }

        return string.Equals(existing.League, league, StringComparison.Ordinal)
            ? existing
            : new ActiveMarketDiscoveryFile { League = league };
    }

    private static void ValidateMarket(ActiveMarketQuoteCapture? market)
    {
        if (market == null)
        {
            return;
        }

        if (market.CaptureId == Guid.Empty || market.CapturedAtUtc == default ||
            market.ImmediateStockRows < 0 || market.CompetingStockRows < 0 ||
            market.ImmediateListedCount < 0 || market.CompetingListedCount < 0 ||
            market.HasTwoSidedBook !=
                (market.ImmediateStockRows > 0 && market.CompetingStockRows > 0) ||
            !RationalExchangeRate.TryCreate(market.RawGet, market.RawGive, out var rate) ||
            rate!.GetUnits != market.GetUnits ||
            rate.GiveUnits != market.GiveUnits ||
            rate.WantedPerOffered != market.WantedPerOffered)
        {
            throw new InvalidDataException(
                "An active-market quote has invalid identity, rate, or depth evidence.");
        }
    }

    private static void RefreshAge(
        ActiveMarketQuoteCapture? market,
        DateTimeOffset generatedAtUtc,
        DateTimeOffset oldestFreshCapture)
    {
        if (market == null)
        {
            return;
        }

        market.AgeSecondsAtGeneration = Math.Max(
            0,
            (long)(generatedAtUtc - market.CapturedAtUtc).TotalSeconds);
        market.FreshAtGeneration = market.CapturedAtUtc >= oldestFreshCapture &&
            market.CapturedAtUtc <= generatedAtUtc;
    }

    private static bool IsNewer(
        ActiveMarketQuoteCapture candidate,
        ActiveMarketQuoteCapture? existing)
    {
        if (existing == null)
        {
            return true;
        }

        var timestampComparison = candidate.CapturedAtUtc.CompareTo(existing.CapturedAtUtc);
        return timestampComparison > 0 ||
            (timestampComparison == 0 && candidate.CaptureId.CompareTo(existing.CaptureId) > 0);
    }

    private static ActiveMarketQuoteCapture? CreateMarket(
        ExchangePairSnapshot? snapshot,
        DateTimeOffset generatedAtUtc,
        DateTimeOffset oldestFreshCapture)
    {
        if (snapshot?.MarketRate == null)
        {
            return null;
        }

        return new ActiveMarketQuoteCapture
        {
            CaptureId = snapshot.CaptureId,
            ScanId = snapshot.ScanId,
            CapturedAtUtc = snapshot.CapturedAtUtc,
            AgeSecondsAtGeneration = Math.Max(
                0,
                (long)(generatedAtUtc - snapshot.CapturedAtUtc).TotalSeconds),
            FreshAtGeneration = snapshot.CapturedAtUtc >= oldestFreshCapture &&
                snapshot.CapturedAtUtc <= generatedAtUtc,
            RawGet = snapshot.MarketRate.RawGet,
            RawGive = snapshot.MarketRate.RawGive,
            GetUnits = snapshot.MarketRate.GetUnits,
            GiveUnits = snapshot.MarketRate.GiveUnits,
            WantedPerOffered = snapshot.MarketRate.WantedPerOffered,
            ImmediateStockRows = snapshot.WantedItemStock.Count,
            CompetingStockRows = snapshot.OfferedItemStock.Count,
            ImmediateListedCount = snapshot.WantedItemStock.Sum(row => (long)row.ListedCount),
            CompetingListedCount = snapshot.OfferedItemStock.Sum(row => (long)row.ListedCount),
            HasTwoSidedBook = snapshot.WantedItemStock.Count > 0 &&
                snapshot.OfferedItemStock.Count > 0
        };
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
}

public sealed class ActiveMarketDiscoveryFile
{
    public int SchemaVersion { get; set; }
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public string League { get; set; } = "";
    public int MaximumQuoteAgeMinutes { get; set; }
    public int CatalogueCurrencyCount { get; set; }
    public long EligibleProbePairCount { get; set; }
    public int ObservedActivePairCount { get; set; }
    public int ActiveCurrencyCount { get; set; }
    public List<ActiveCurrencyMarketCapture> Currencies { get; set; } = [];
}

public sealed class ActiveCurrencyMarketCapture
{
    public CurrencyCapture Currency { get; set; } = new();
    public string ObservedMarkets { get; set; } = "";
    public List<string> ObservedDirections { get; set; } = [];
    public ActiveMarketQuoteCapture? ChaosMarket { get; set; }
    public ActiveMarketQuoteCapture? DivineMarket { get; set; }
    public ActiveMarketQuoteCapture? ChaosBuyMarket { get; set; }
    public ActiveMarketQuoteCapture? DivineBuyMarket { get; set; }
}

public sealed class ActiveMarketQuoteCapture
{
    public Guid CaptureId { get; set; }
    public Guid? ScanId { get; set; }
    public DateTimeOffset CapturedAtUtc { get; set; }
    public long AgeSecondsAtGeneration { get; set; }
    public bool FreshAtGeneration { get; set; }
    public int RawGet { get; set; }
    public int RawGive { get; set; }
    public int GetUnits { get; set; }
    public int GiveUnits { get; set; }
    public decimal WantedPerOffered { get; set; }
    public int ImmediateStockRows { get; set; }
    public int CompetingStockRows { get; set; }
    public long ImmediateListedCount { get; set; }
    public long CompetingListedCount { get; set; }
    public bool HasTwoSidedBook { get; set; }
}
