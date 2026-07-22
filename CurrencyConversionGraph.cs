using Newtonsoft.Json;

namespace FaustusController;

public readonly record struct CurrencyConversionGraphResult(
    int VertexCount,
    int EdgeCount,
    int CurrentLeagueCaptureCount,
    int ExcludedNullRateCount,
    int ExcludedUnknownCurrencyCount,
    int ExcludedFutureCount,
    int ExcludedStaleCount,
    int ExcludedIncoherentCount,
    int ExcludedManualSkipCount,
    DateTimeOffset? NextExpirationAtUtc);

public sealed class CurrencyConversionGraphExporter
{
    private const int CurrentSchemaVersion = 1;

    public CurrencyConversionGraphResult Export(
        CurrencyCatalogue catalogue,
        IReadOnlyCollection<ExchangePairSnapshot> snapshots,
        IReadOnlyCollection<CurrencyDiscoveryProbeOutcome> probeOutcomes,
        LatestBoundedScanManifest? latestBoundedScan,
        string league,
        DateTimeOffset generatedAtUtc,
        TimeSpan maximumQuoteAge,
        string outputPath,
        int preExcludedIncoherentCount = 0,
        IReadOnlySet<string>? skippedCurrencyMetadata = null)
    {
        if (string.IsNullOrWhiteSpace(league) || maximumQuoteAge < TimeSpan.Zero)
        {
            throw new ArgumentException("Graph export requires a league and nonnegative quote age.");
        }

        var catalogueByMetadata = catalogue.Items.ToDictionary(
            currency => currency.Metadata,
            StringComparer.Ordinal);
        var currentLeagueSnapshots = snapshots
            .Where(snapshot => string.Equals(snapshot.League, league, StringComparison.Ordinal) &&
                IsCanonicalPair(catalogue, snapshot.Pair))
            .ToArray();
        if (currentLeagueSnapshots.Select(snapshot => snapshot.Pair).Distinct().Count() !=
            currentLeagueSnapshots.Length)
        {
            throw new InvalidDataException(
                "Conversion graph input contains duplicate latest directed pairs.");
        }

        var outcomesByPair = probeOutcomes
            .Where(outcome => string.Equals(outcome.League, league, StringComparison.Ordinal))
            .ToDictionary(outcome => outcome.Pair);
        var completedManifestReferences = GetCompletedManifestReferences(
            latestBoundedScan,
            league);
        var oldestAllowed = generatedAtUtc - maximumQuoteAge;
        var edges = new List<CurrencyConversionGraphEdgeCapture>();
        var excludedNullRate = 0;
        var excludedUnknownCurrency = 0;
        var excludedFuture = 0;
        var excludedStale = 0;
        var excludedIncoherent = preExcludedIncoherentCount;
        var excludedManualSkip = 0;

        foreach (var snapshot in currentLeagueSnapshots)
        {
            if (!catalogueByMetadata.ContainsKey(snapshot.OfferedCurrency.Metadata) ||
                !catalogueByMetadata.ContainsKey(snapshot.WantedCurrency.Metadata) ||
                snapshot.OfferedCurrency.Metadata == snapshot.WantedCurrency.Metadata)
            {
                excludedUnknownCurrency++;
                continue;
            }

            if (skippedCurrencyMetadata != null &&
                (skippedCurrencyMetadata.Contains(snapshot.OfferedCurrency.Metadata) ||
                    skippedCurrencyMetadata.Contains(snapshot.WantedCurrency.Metadata)))
            {
                excludedManualSkip++;
                continue;
            }

            if (snapshot.CapturedAtUtc > generatedAtUtc)
            {
                excludedFuture++;
                continue;
            }

            if (snapshot.CapturedAtUtc < oldestAllowed)
            {
                excludedStale++;
                continue;
            }

            if (!TryResolveCoherence(
                snapshot,
                outcomesByPair,
                completedManifestReferences,
                latestBoundedScan,
                out var coherence))
            {
                excludedIncoherent++;
                continue;
            }

            var candidateEdges = CreateEdges(snapshot, coherence, generatedAtUtc).ToArray();
            if (candidateEdges.Length == 0)
            {
                excludedNullRate++;
                continue;
            }

            edges.AddRange(candidateEdges);
        }

        var file = new CurrencyConversionGraphFile
        {
            SchemaVersion = CurrentSchemaVersion,
            GeneratedAtUtc = generatedAtUtc,
            League = league,
            MaximumQuoteAgeMinutes = (int)maximumQuoteAge.TotalMinutes,
            VertexCount = catalogue.Items.Count,
            EdgeCount = SelectLatestEdges(edges).Count,
            CurrentLeagueCaptureCount =
                currentLeagueSnapshots.Length + preExcludedIncoherentCount,
            ExcludedNullRateCount = excludedNullRate,
            ExcludedUnknownCurrencyCount = excludedUnknownCurrency,
            ExcludedFutureCount = excludedFuture,
            ExcludedStaleCount = excludedStale,
            ExcludedIncoherentCount = excludedIncoherent,
            ExcludedManualSkipCount = excludedManualSkip,
            Vertices = catalogue.Items.Select(CreateCurrency).ToList(),
            Edges = SelectLatestEdges(edges)
        };
        ValidateFile(file);
        DateTimeOffset? nextExpirationAtUtc = edges.Count == 0
            ? null
            : edges.Min(edge => edge.CapturedAtUtc + maximumQuoteAge) +
                TimeSpan.FromMilliseconds(1);

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
        return new CurrencyConversionGraphResult(
            file.VertexCount,
            file.EdgeCount,
            file.CurrentLeagueCaptureCount,
            file.ExcludedNullRateCount,
            file.ExcludedUnknownCurrencyCount,
            file.ExcludedFutureCount,
            file.ExcludedStaleCount,
            file.ExcludedIncoherentCount,
            file.ExcludedManualSkipCount,
            nextExpirationAtUtc);
    }

    private static List<CurrencyConversionGraphEdgeCapture> SelectLatestEdges(
        IReadOnlyCollection<CurrencyConversionGraphEdgeCapture> edges)
    {
        return edges
            .GroupBy(edge => new CurrencyPairKey(edge.OfferedMetadata, edge.WantedMetadata))
            .Select(group => group
                .OrderByDescending(edge => edge.CapturedAtUtc)
                .ThenByDescending(edge => edge.CaptureId)
                .First())
            .OrderBy(edge => edge.OfferedMetadata, StringComparer.Ordinal)
            .ThenBy(edge => edge.WantedMetadata, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsCanonicalPair(CurrencyCatalogue catalogue, CurrencyPairKey pair)
    {
        return (catalogue.TryGetUniqueByName("Chaos Orb", out var chaos) &&
                pair.WantedMetadata == chaos!.Metadata &&
                pair.OfferedMetadata != chaos.Metadata) ||
            (catalogue.TryGetUniqueByName("Divine Orb", out var divine) &&
                pair.WantedMetadata == divine!.Metadata &&
                pair.OfferedMetadata != divine.Metadata);
    }

    private static Dictionary<CurrencyPairKey, ScanCaptureReferenceCapture>
        GetCompletedManifestReferences(
            LatestBoundedScanManifest? manifest,
            string league)
    {
        if (manifest == null || manifest.State != "Completed" ||
            !string.Equals(manifest.League, league, StringComparison.Ordinal))
        {
            return [];
        }

        if (manifest.ScanId == Guid.Empty || manifest.CollectorSessionId == Guid.Empty ||
            manifest.PersistedCaptures.Count == 0 ||
            manifest.PersistedCaptures.Count != manifest.PlannedPairs.Count)
        {
            return [];
        }

        var references = new Dictionary<CurrencyPairKey, ScanCaptureReferenceCapture>();
        for (var index = 0; index < manifest.PersistedCaptures.Count; index++)
        {
            var planned = manifest.PlannedPairs[index];
            var reference = manifest.PersistedCaptures[index];
            var pair = new CurrencyPairKey(
                reference.OfferedMetadata,
                reference.WantedMetadata);
            if (planned.Sequence != index + 1 || reference.Sequence != index + 1 ||
                planned.OfferedMetadata != reference.OfferedMetadata ||
                planned.WantedMetadata != reference.WantedMetadata ||
                string.IsNullOrWhiteSpace(reference.OfferedMetadata) ||
                string.IsNullOrWhiteSpace(reference.WantedMetadata) ||
                reference.OfferedMetadata == reference.WantedMetadata ||
                reference.CaptureId == Guid.Empty || !references.TryAdd(pair, reference))
            {
                return [];
            }
        }

        return references;
    }

    private static bool TryResolveCoherence(
        ExchangePairSnapshot snapshot,
        IReadOnlyDictionary<CurrencyPairKey, CurrencyDiscoveryProbeOutcome> outcomesByPair,
        IReadOnlyDictionary<CurrencyPairKey, ScanCaptureReferenceCapture> manifestReferences,
        LatestBoundedScanManifest? manifest,
        out string coherence)
    {
        if (snapshot.Source == ExchangeCaptureSource.LiquidityDiscoveryAutomation &&
            snapshot.CollectorSessionId != Guid.Empty &&
            snapshot.ScanId is { } discoveryRunId &&
            discoveryRunId != Guid.Empty &&
            snapshot.ScanSequence is { } discoverySequence &&
            discoverySequence > 0 &&
            outcomesByPair.TryGetValue(snapshot.Pair, out var outcome) &&
            outcome.Status == CurrencyDiscoveryProbeStatus.Active &&
            outcome.CaptureId == snapshot.CaptureId &&
            outcome.RunId == discoveryRunId &&
            outcome.RunSequence == discoverySequence &&
            outcome.ObservedAtUtc == snapshot.CapturedAtUtc)
        {
            coherence = "ActiveDiscoveryProbe";
            return true;
        }

        if (snapshot.Source == ExchangeCaptureSource.BoundedScanAutomation &&
            snapshot.CollectorSessionId != Guid.Empty &&
            manifest != null && manifest.State == "Completed" &&
            manifest.ScanId != Guid.Empty && manifest.CollectorSessionId != Guid.Empty &&
            snapshot.ScanId == manifest.ScanId &&
            snapshot.CollectorSessionId == manifest.CollectorSessionId &&
            snapshot.ScanSequence is { } scanSequence &&
            manifestReferences.TryGetValue(snapshot.Pair, out var reference) &&
            reference.CaptureId == snapshot.CaptureId &&
            reference.Sequence == scanSequence)
        {
            coherence = "CompletedBoundedScan";
            return true;
        }

        coherence = "";
        return false;
    }

    private static IEnumerable<CurrencyConversionGraphEdgeCapture> CreateEdges(
        ExchangePairSnapshot snapshot,
        string coherence,
        DateTimeOffset generatedAtUtc)
    {
        if ((snapshot.TopImmediateStock?.SelectedPairRate ?? snapshot.MarketRate) is { } immediateRate)
        {
            yield return CreateEdge(
                snapshot,
                snapshot.OfferedCurrency.Metadata,
                snapshot.WantedCurrency.Metadata,
                immediateRate,
                snapshot.TopImmediateStock?.ListedCount ?? 0,
                coherence,
                "ImmediateBook",
                generatedAtUtc);
        }

        if (snapshot.TopCompetingStock?.RawRate is { } competingRate)
        {
            yield return CreateEdge(
                snapshot,
                snapshot.WantedCurrency.Metadata,
                snapshot.OfferedCurrency.Metadata,
                competingRate,
                snapshot.TopCompetingStock?.ListedCount ?? 0,
                coherence,
                "CompetingBook",
                generatedAtUtc);
        }
    }

    private static CurrencyConversionGraphEdgeCapture CreateEdge(
        ExchangePairSnapshot snapshot,
        string offeredMetadata,
        string wantedMetadata,
        RationalExchangeRate rate,
        int listedCount,
        string coherence,
        string bookSide,
        DateTimeOffset generatedAtUtc)
    {
        return new CurrencyConversionGraphEdgeCapture
        {
            OfferedMetadata = offeredMetadata,
            WantedMetadata = wantedMetadata,
            CaptureId = snapshot.CaptureId,
            CapturedAtUtc = snapshot.CapturedAtUtc,
            AgeSecondsAtGeneration = (long)(generatedAtUtc - snapshot.CapturedAtUtc).TotalSeconds,
            CollectorSessionId = snapshot.CollectorSessionId,
            ScanId = snapshot.ScanId,
            ScanSequence = snapshot.ScanSequence,
            Source = snapshot.Source.ToString(),
            Coherence = coherence,
            BookSide = bookSide,
            RawGet = rate.RawGet,
            RawGive = rate.RawGive,
            GetUnits = rate.GetUnits,
            GiveUnits = rate.GiveUnits,
            WantedPerOffered = rate.WantedPerOffered,
            ListedCount = listedCount
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

    private static void ValidateFile(CurrencyConversionGraphFile file)
    {
        if (file.SchemaVersion != CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(file.League) || file.GeneratedAtUtc == default ||
            file.VertexCount != file.Vertices.Count || file.EdgeCount != file.Edges.Count ||
            file.MaximumQuoteAgeMinutes < 0 || file.CurrentLeagueCaptureCount < 0 ||
            file.ExcludedNullRateCount < 0 || file.ExcludedUnknownCurrencyCount < 0 ||
            file.ExcludedFutureCount < 0 || file.ExcludedStaleCount < 0 ||
            file.ExcludedIncoherentCount < 0 || file.ExcludedManualSkipCount < 0 ||
            file.CurrentLeagueCaptureCount < 0)
        {
            throw new InvalidDataException("Conversion graph root metadata is invalid.");
        }

        var vertices = file.Vertices.ToDictionary(
            vertex => vertex.Metadata,
            StringComparer.Ordinal);
        if (vertices.Count != file.Vertices.Count ||
            vertices.Values.Any(vertex => string.IsNullOrWhiteSpace(vertex.Name)))
        {
            throw new InvalidDataException("Conversion graph vertices are invalid or duplicated.");
        }

        var pairs = new HashSet<CurrencyPairKey>();
        foreach (var edge in file.Edges)
        {
            var pair = new CurrencyPairKey(edge.OfferedMetadata, edge.WantedMetadata);
            if (!vertices.ContainsKey(edge.OfferedMetadata) ||
                !vertices.ContainsKey(edge.WantedMetadata) ||
                edge.OfferedMetadata == edge.WantedMetadata || !pairs.Add(pair) ||
                edge.CaptureId == Guid.Empty ||
                edge.CapturedAtUtc == default || edge.AgeSecondsAtGeneration < 0 ||
                edge.CollectorSessionId == Guid.Empty || !edge.ScanId.HasValue ||
                edge.ScanId.Value == Guid.Empty || edge.ScanSequence is null or <= 0 ||
                edge.RawGet <= 0 || edge.RawGive <= 0 || edge.GetUnits <= 0 ||
                edge.GiveUnits <= 0 ||
                !RationalExchangeRate.TryCreate(edge.RawGet, edge.RawGive, out var rate) ||
                rate!.GetUnits != edge.GetUnits || rate.GiveUnits != edge.GiveUnits ||
                rate.WantedPerOffered != edge.WantedPerOffered ||
                edge.ListedCount < 0 ||
                edge.BookSide is not "ImmediateBook" and not "CompetingBook" ||
                edge.Coherence is not "ActiveDiscoveryProbe" and not "CompletedBoundedScan")
            {
                throw new InvalidDataException("Conversion graph contains an invalid edge.");
            }
        }
    }
}

public sealed class CurrencyConversionGraphFile
{
    public int SchemaVersion { get; set; }
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public string League { get; set; } = "";
    public int MaximumQuoteAgeMinutes { get; set; }
    public int VertexCount { get; set; }
    public int EdgeCount { get; set; }
    public int CurrentLeagueCaptureCount { get; set; }
    public int ExcludedNullRateCount { get; set; }
    public int ExcludedUnknownCurrencyCount { get; set; }
    public int ExcludedFutureCount { get; set; }
    public int ExcludedStaleCount { get; set; }
    public int ExcludedIncoherentCount { get; set; }
    public int ExcludedManualSkipCount { get; set; }
    public List<CurrencyCapture> Vertices { get; set; } = [];
    public List<CurrencyConversionGraphEdgeCapture> Edges { get; set; } = [];
}

public sealed class CurrencyConversionGraphEdgeCapture
{
    public string OfferedMetadata { get; set; } = "";
    public string WantedMetadata { get; set; } = "";
    public Guid CaptureId { get; set; }
    public DateTimeOffset CapturedAtUtc { get; set; }
    public long AgeSecondsAtGeneration { get; set; }
    public Guid CollectorSessionId { get; set; }
    public Guid? ScanId { get; set; }
    public int? ScanSequence { get; set; }
    public string Source { get; set; } = "";
    public string Coherence { get; set; } = "";
    public string BookSide { get; set; } = "";
    public int RawGet { get; set; }
    public int RawGive { get; set; }
    public int GetUnits { get; set; }
    public int GiveUnits { get; set; }
    public decimal WantedPerOffered { get; set; }
    public int ListedCount { get; set; }
}
