using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FaustusController;

public readonly record struct RateCaptureExportResult(int CaptureCount);
public readonly record struct GraphSnapshotLoadResult(
    IReadOnlyCollection<ExchangePairSnapshot> Snapshots,
    int ExcludedMalformedCount);

public sealed class RateCaptureJsonExporter
{
    private const int CurrentSchemaVersion = 4;
    private const string LegacyLeague = "unknown-schema-v1";

    public RateCaptureExportResult Export(
        IReadOnlyCollection<ExchangePairSnapshot> snapshots,
        string outputPath,
        BoundedScanProgress? boundedScanProgress = null,
        Guid activeCollectorSessionId = default)
    {
        var latestByPair = SelectLatest(LoadExistingSnapshots(outputPath));
        var latestBoundedScan = LoadExistingManifest(outputPath);
        var latestCompletedBoundedScan = LoadExistingCompletedManifest(outputPath);
        foreach (var snapshot in snapshots)
        {
            ValidateSnapshot(snapshot);
            latestByPair[snapshot.LeaguePair] = snapshot;
        }

        ValidateGlobalIdentities(latestByPair.Values);
        var replacingManifest = boundedScanProgress != null;
        if (boundedScanProgress != null)
        {
            latestBoundedScan = CreateManifest(boundedScanProgress);
            if (boundedScanProgress.State == BoundedScanState.Completed)
            {
                latestCompletedBoundedScan = CreateManifest(boundedScanProgress);
            }
        }

        latestBoundedScan = ValidateAndReconcileManifest(
            latestBoundedScan,
            latestByPair,
            replacingManifest,
            activeCollectorSessionId);
        latestCompletedBoundedScan = ValidateAndReconcileManifest(
            latestCompletedBoundedScan,
            latestByPair,
            replacingManifest: false,
            activeCollectorSessionId: activeCollectorSessionId);
        var export = new RateCaptureExportFile
        {
            SchemaVersion = CurrentSchemaVersion,
            ExportedAtUtc = DateTimeOffset.UtcNow,
            Captures = latestByPair.Values
                .OrderBy(snapshot => snapshot.League, StringComparer.Ordinal)
                .ThenBy(snapshot => snapshot.OfferedCurrency.Metadata, StringComparer.Ordinal)
                .ThenBy(snapshot => snapshot.WantedCurrency.Metadata, StringComparer.Ordinal)
                .Select(CreateCapture)
                .ToList(),
            LatestBoundedScan = latestBoundedScan,
            LatestCompletedBoundedScan = latestCompletedBoundedScan
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
        return new RateCaptureExportResult(export.Captures.Count);
    }

    public IReadOnlyCollection<ExchangePairSnapshot> LoadSnapshots(string inputPath)
    {
        return SelectLatest(LoadExistingSnapshots(inputPath)).Values
            .OrderBy(snapshot => snapshot.CapturedAtUtc)
            .ThenBy(snapshot => snapshot.CaptureId)
            .ToArray();
    }

    public GraphSnapshotLoadResult LoadSnapshotsForGraph(
        string inputPath,
        string league)
    {
        if (!File.Exists(inputPath))
        {
            return new GraphSnapshotLoadResult([], 0);
        }

        var json = File.ReadAllText(inputPath);
        var document = JObject.Parse(json);
        if (document.Value<int?>(nameof(RateCaptureExportFile.SchemaVersion)) !=
            CurrentSchemaVersion)
        {
            var snapshots = LoadSnapshots(inputPath)
                .Where(snapshot => string.Equals(
                    snapshot.League,
                    league,
                    StringComparison.Ordinal))
                .ToArray();
            return new GraphSnapshotLoadResult(snapshots, 0);
        }

        var file = JsonConvert.DeserializeObject<RateCaptureExportFile>(json) ??
            throw new InvalidDataException("The schema-v4 rate export is empty.");
        var rawCurrentLeague = file.Captures
            .Where(capture => string.Equals(capture.League, league, StringComparison.Ordinal))
            .ToArray();
        var restored = new List<(int Index, ExchangePairSnapshot Snapshot)>();
        for (var index = 0; index < rawCurrentLeague.Length; index++)
        {
            try
            {
                var snapshot = RestoreCapture(rawCurrentLeague[index]);
                ValidateSnapshot(snapshot);
                restored.Add((index, snapshot));
            }
            catch
            {
                // The primary loader remains strict; graph projection degrades bad captures to exclusions.
            }
        }

        var invalidIndexes = new HashSet<int>();
        MarkDuplicateGroups(restored, item => item.Snapshot.LeaguePair, invalidIndexes);
        MarkDuplicateGroups(restored, item => item.Snapshot.CaptureId, invalidIndexes);
        MarkDuplicateGroups(
            restored.Where(item => item.Snapshot.ScanId != null),
            item => (item.Snapshot.ScanId!.Value, item.Snapshot.ScanSequence!.Value),
            invalidIndexes);
        foreach (var group in restored
            .Where(item => item.Snapshot.ScanId != null)
            .GroupBy(item => item.Snapshot.ScanId!.Value))
        {
            if (group.Select(item => (
                    item.Snapshot.CollectorSessionId,
                    item.Snapshot.League,
                    item.Snapshot.Source))
                .Distinct()
                .Count() > 1)
            {
                foreach (var item in group)
                {
                    invalidIndexes.Add(item.Index);
                }
            }
        }

        var valid = restored
            .Where(item => !invalidIndexes.Contains(item.Index))
            .Select(item => item.Snapshot)
            .ToArray();
        return new GraphSnapshotLoadResult(
            valid,
            rawCurrentLeague.Length - valid.Length);
    }

    public LatestBoundedScanManifest? LoadLatestCompletedBoundedScan(string inputPath)
    {
        var manifest = LoadExistingCompletedManifest(inputPath);
        if (manifest != null)
        {
            ValidateManifestStructure(manifest);
        }

        return manifest;
    }

    private static IReadOnlyCollection<ExchangePairSnapshot> LoadExistingSnapshots(
        string inputPath)
    {
        if (!File.Exists(inputPath))
        {
            return [];
        }

        var json = File.ReadAllText(inputPath);
        var document = JObject.Parse(json);
        var schemaVersion = document.Value<int?>(nameof(RateCaptureExportFile.SchemaVersion));
        var snapshots = schemaVersion switch
        {
            CurrentSchemaVersion => RestoreSchema4(json),
            3 => RestoreSchema3(json),
            2 => RestoreSchema2(json),
            1 => RestoreSchema1(json),
            null => throw new InvalidDataException("The existing rate export has no schema version."),
            _ => throw new InvalidDataException(
                $"Unsupported rate capture schema version {schemaVersion}.")
        };

        foreach (var snapshot in snapshots)
        {
            ValidateSnapshot(snapshot);
        }

        ValidateGlobalIdentities(snapshots);
        if (schemaVersion >= 3 &&
            snapshots.Select(snapshot => snapshot.LeaguePair).Distinct().Count() != snapshots.Count)
        {
            throw new InvalidDataException(
                "The latest rate export contains a duplicate league/directed-pair capture.");
        }

        return snapshots;
    }

    private static LatestBoundedScanManifest? LoadExistingManifest(string inputPath)
    {
        if (!File.Exists(inputPath))
        {
            return null;
        }

        var json = File.ReadAllText(inputPath);
        var document = JObject.Parse(json);
        return document.Value<int?>(nameof(RateCaptureExportFile.SchemaVersion)) ==
            CurrentSchemaVersion
                ? JsonConvert.DeserializeObject<RateCaptureExportFile>(json)?.LatestBoundedScan
                : null;
    }

    private static LatestBoundedScanManifest? LoadExistingCompletedManifest(string inputPath)
    {
        if (!File.Exists(inputPath))
        {
            return null;
        }

        var json = File.ReadAllText(inputPath);
        if (JObject.Parse(json).Value<int?>(nameof(RateCaptureExportFile.SchemaVersion)) !=
            CurrentSchemaVersion)
        {
            return null;
        }

        var file = JsonConvert.DeserializeObject<RateCaptureExportFile>(json);
        return file?.LatestCompletedBoundedScan ??
            (file?.LatestBoundedScan?.State == "Completed"
                ? file.LatestBoundedScan
                : null);
    }

    private static Dictionary<LeagueCurrencyPairKey, ExchangePairSnapshot> SelectLatest(
        IEnumerable<ExchangePairSnapshot> snapshots)
    {
        var result = new Dictionary<LeagueCurrencyPairKey, ExchangePairSnapshot>();
        foreach (var snapshot in snapshots)
        {
            if (!result.TryGetValue(snapshot.LeaguePair, out var existing) ||
                CompareObservationOrder(snapshot, existing) > 0)
            {
                result[snapshot.LeaguePair] = snapshot;
            }
        }

        return result;
    }

    private static void MarkDuplicateGroups<TKey>(
        IEnumerable<(int Index, ExchangePairSnapshot Snapshot)> snapshots,
        Func<(int Index, ExchangePairSnapshot Snapshot), TKey> keySelector,
        ISet<int> invalidIndexes)
        where TKey : notnull
    {
        foreach (var group in snapshots.GroupBy(keySelector).Where(group => group.Count() > 1))
        {
            foreach (var item in group)
            {
                invalidIndexes.Add(item.Index);
            }
        }
    }

    private static IReadOnlyCollection<ExchangePairSnapshot> RestoreSchema4(string json)
    {
        var export = JsonConvert.DeserializeObject<RateCaptureExportFile>(json) ??
            throw new InvalidDataException("The schema-v4 rate export is empty.");
        return export.Captures.Select(RestoreCapture).ToArray();
    }

    private static IReadOnlyCollection<ExchangePairSnapshot> RestoreSchema3(string json)
    {
        var export = JsonConvert.DeserializeObject<LegacyLatestRateCaptureExportFile>(json) ??
            throw new InvalidDataException("The schema-v3 rate export is empty.");
        return export.Captures.Select(RestoreCapture).ToArray();
    }

    private static IReadOnlyCollection<ExchangePairSnapshot> RestoreSchema2(string json)
    {
        var export = JsonConvert.DeserializeObject<LegacyRateHistoryExportFile>(json) ??
            throw new InvalidDataException("The schema-v2 rate history export is empty.");
        var snapshots = new List<ExchangePairSnapshot>();
        foreach (var series in export.Series)
        {
            ValidateIdentity(series.League, series.OfferedCurrency, series.WantedCurrency);
            foreach (var capture in series.History)
            {
                snapshots.Add(RestoreObservation(
                    series.League,
                    series.OfferedCurrency,
                    series.WantedCurrency,
                    capture));
            }
        }

        return snapshots;
    }

    private static IReadOnlyCollection<ExchangePairSnapshot> RestoreSchema1(string json)
    {
        var export = JsonConvert.DeserializeObject<LegacyRateCaptureExportFile>(json) ??
            throw new InvalidDataException("The schema-v1 rate capture export is empty.");
        var snapshots = new List<ExchangePairSnapshot>();
        foreach (var capture in export.Captures)
        {
            ValidateIdentity(
                LegacyLeague,
                capture.OfferedCurrency,
                capture.WantedCurrency);
            snapshots.Add(RestoreObservation(
                LegacyLeague,
                capture.OfferedCurrency,
                capture.WantedCurrency,
                new RateCaptureObservation
                {
                    CaptureId = Guid.NewGuid(),
                    CapturedAtUtc = capture.CapturedAtUtc,
                    CollectorSessionId = Guid.Empty,
                    Source = ExchangeCaptureSource.LegacySchema1.ToString(),
                    AreaInstanceId = 0,
                    MarketRate = capture.MarketRate,
                    TopImmediateRate = capture.TopImmediateRate,
                    TopCompetingRate = capture.TopCompetingRate,
                    WantedItemStock = capture.WantedItemStock,
                    OfferedItemStock = capture.OfferedItemStock
                }));
        }

        return snapshots;
    }

    private static ExchangePairSnapshot RestoreCapture(LatestRateCapture capture)
    {
        ValidateIdentity(capture.League, capture.OfferedCurrency, capture.WantedCurrency);
        return RestoreObservation(
            capture.League,
            capture.OfferedCurrency,
            capture.WantedCurrency,
            capture);
    }

    private static ExchangePairSnapshot RestoreObservation(
        string league,
        CurrencyCapture offeredCurrency,
        CurrencyCapture wantedCurrency,
        RateCaptureObservation capture)
    {
        if (!Enum.GetNames<ExchangeCaptureSource>().Contains(
                capture.Source,
                StringComparer.Ordinal) ||
            !Enum.TryParse<ExchangeCaptureSource>(capture.Source, out var source) ||
            !Enum.IsDefined(source))
        {
            throw new InvalidDataException($"Unknown rate capture source '{capture.Source}'.");
        }

        var snapshot = new ExchangePairSnapshot(
            capture.CaptureId,
            capture.CapturedAtUtc,
            league,
            capture.AreaInstanceId,
            new CurrencyIdentity(
                offeredCurrency.Metadata,
                offeredCurrency.Hash,
                offeredCurrency.Name),
            new CurrencyIdentity(
                wantedCurrency.Metadata,
                wantedCurrency.Hash,
                wantedCurrency.Name),
            RestoreRate(capture.MarketRate),
            RestoreStock(capture.WantedItemStock, ExchangeStockSide.WantedItem),
            RestoreStock(capture.OfferedItemStock, ExchangeStockSide.OfferedItem))
        {
            CollectorSessionId = capture.CollectorSessionId,
            ScanId = capture.ScanId,
            ScanSequence = capture.ScanSequence,
            Source = source
        };

        ValidateRateCapture(capture.TopImmediateRate, snapshot.TopImmediateRate);
        ValidateRateCapture(capture.TopCompetingRate, snapshot.TopCompetingRate);
        return snapshot;
    }

    private static LatestRateCapture CreateCapture(ExchangePairSnapshot snapshot)
    {
        return new LatestRateCapture
        {
            League = snapshot.League,
            OfferedCurrency = CreateCurrency(snapshot.OfferedCurrency),
            WantedCurrency = CreateCurrency(snapshot.WantedCurrency),
            CaptureId = snapshot.CaptureId,
            CapturedAtUtc = snapshot.CapturedAtUtc,
            CollectorSessionId = snapshot.CollectorSessionId,
            ScanId = snapshot.ScanId,
            ScanSequence = snapshot.ScanSequence,
            Source = snapshot.Source.ToString(),
            AreaInstanceId = snapshot.AreaInstanceId,
            MarketRate = CreateRate(snapshot.MarketRate),
            TopImmediateRate = CreateRate(snapshot.TopImmediateRate),
            TopCompetingRate = CreateRate(snapshot.TopCompetingRate),
            WantedItemStock = snapshot.WantedItemStock.Select(CreateStock).ToList(),
            OfferedItemStock = snapshot.OfferedItemStock.Select(CreateStock).ToList()
        };
    }

    private static void ValidateSnapshot(ExchangePairSnapshot snapshot)
    {
        if (snapshot.CaptureId == Guid.Empty || snapshot.CapturedAtUtc == default ||
            string.IsNullOrWhiteSpace(snapshot.League))
        {
            throw new InvalidDataException(
                "A rate snapshot requires capture, timestamp, and league identity.");
        }

        ValidateCaptureContext(
            snapshot.Source,
            snapshot.League,
            snapshot.CollectorSessionId,
            snapshot.ScanId,
            snapshot.ScanSequence);
    }

    private static void ValidateIdentity(
        string league,
        CurrencyCapture offeredCurrency,
        CurrencyCapture wantedCurrency)
    {
        if (string.IsNullOrWhiteSpace(league) ||
            string.IsNullOrWhiteSpace(offeredCurrency.Metadata) ||
            string.IsNullOrWhiteSpace(wantedCurrency.Metadata) ||
            string.IsNullOrWhiteSpace(offeredCurrency.Name) ||
            string.IsNullOrWhiteSpace(wantedCurrency.Name) ||
            offeredCurrency.Metadata == wantedCurrency.Metadata)
        {
            throw new InvalidDataException("A rate capture has an invalid pair identity.");
        }
    }

    private static void ValidateCaptureContext(
        ExchangeCaptureSource source,
        string league,
        Guid collectorSessionId,
        Guid? scanId,
        int? scanSequence)
    {
        var hasScanId = scanId is { } value && value != Guid.Empty;
        switch (source)
        {
            case ExchangeCaptureSource.LegacySchema1:
                if (league != LegacyLeague || collectorSessionId != Guid.Empty ||
                    scanId != null || scanSequence != null)
                {
                    throw new InvalidDataException(
                        "Legacy schema-v1 capture context is inconsistent.");
                }

                return;
            case ExchangeCaptureSource.BoundedScanAutomation:
            case ExchangeCaptureSource.LiquidityDiscoveryAutomation:
                if (collectorSessionId == Guid.Empty || !hasScanId || scanSequence is null or <= 0)
                {
                    throw new InvalidDataException(
                        "Bounded-scan capture context is incomplete.");
                }

                return;
            case ExchangeCaptureSource.Manual:
            case ExchangeCaptureSource.SinglePairAutomation:
                if (collectorSessionId == Guid.Empty || scanId != null || scanSequence != null)
                {
                    throw new InvalidDataException(
                        "Non-bounded capture context contains invalid scan metadata.");
                }

                return;
            default:
                throw new InvalidDataException($"Unsupported capture source '{source}'.");
        }
    }

    private static void ValidateGlobalIdentities(
        IEnumerable<ExchangePairSnapshot> snapshots)
    {
        var captureIds = new HashSet<Guid>();
        var scanContextById = new Dictionary<
            Guid,
            (Guid CollectorSessionId, string League, ExchangeCaptureSource Source)>();
        var scanSequences = new HashSet<(Guid ScanId, int Sequence)>();
        foreach (var snapshot in snapshots)
        {
            if (!captureIds.Add(snapshot.CaptureId))
            {
                throw new InvalidDataException("A capture ID appears more than once.");
            }

            if (snapshot.ScanId is not { } scanId)
            {
                continue;
            }

            var context = (
                snapshot.CollectorSessionId,
                snapshot.League,
                snapshot.Source);
            if (scanContextById.TryGetValue(scanId, out var priorContext) &&
                priorContext != context)
            {
                throw new InvalidDataException(
                    "A scan ID spans multiple collector sessions, leagues, or capture sources.");
            }

            scanContextById[scanId] = context;
            if (!scanSequences.Add((scanId, snapshot.ScanSequence!.Value)))
            {
                throw new InvalidDataException(
                    "A bounded scan contains a duplicate sequence number.");
            }
        }
    }

    private static int CompareObservationOrder(
        ExchangePairSnapshot left,
        ExchangePairSnapshot right)
    {
        var timestampComparison = left.CapturedAtUtc.CompareTo(right.CapturedAtUtc);
        return timestampComparison != 0
            ? timestampComparison
            : left.CaptureId.CompareTo(right.CaptureId);
    }

    private static LatestBoundedScanManifest CreateManifest(BoundedScanProgress progress)
    {
        return new LatestBoundedScanManifest
        {
            ScanId = progress.ScanId,
            CollectorSessionId = progress.CollectorSessionId,
            League = progress.League,
            StartedAtUtc = progress.StartedAtUtc,
            UpdatedAtUtc = progress.UpdatedAtUtc,
            TerminalAtUtc = progress.TerminalAtUtc,
            State = progress.State.ToString(),
            FailureReason = progress.FailureReason,
            PlannedPairs = progress.PlannedPairs.Select(item => new ScanPairReferenceCapture
            {
                Sequence = item.Sequence,
                OfferedMetadata = item.Pair.OfferedMetadata,
                WantedMetadata = item.Pair.WantedMetadata
            }).ToList(),
            PersistedCaptures = progress.PersistedCaptures.Select(
                item => new ScanCaptureReferenceCapture
                {
                    Sequence = item.Sequence,
                    CaptureId = item.CaptureId,
                    OfferedMetadata = item.Pair.OfferedMetadata,
                    WantedMetadata = item.Pair.WantedMetadata
                }).ToList()
        };
    }

    private static LatestBoundedScanManifest? ValidateAndReconcileManifest(
        LatestBoundedScanManifest? manifest,
        IReadOnlyDictionary<LeagueCurrencyPairKey, ExchangePairSnapshot> latestByPair,
        bool replacingManifest,
        Guid activeCollectorSessionId)
    {
        if (manifest == null)
        {
            return null;
        }

        ValidateManifestStructure(manifest);
        if (!replacingManifest && activeCollectorSessionId != Guid.Empty &&
            manifest.CollectorSessionId != activeCollectorSessionId &&
            manifest.State is not "Completed" and not "Faulted" and not "Invalidated")
        {
            manifest.State = "Faulted";
            manifest.UpdatedAtUtc = DateTimeOffset.UtcNow;
            manifest.TerminalAtUtc = manifest.UpdatedAtUtc;
            manifest.FailureReason = "Scan was interrupted by plugin reload or process exit.";
        }

        var allReferencesCurrent = manifest.PersistedCaptures.All(reference =>
        {
            var key = new LeagueCurrencyPairKey(
                manifest.League,
                reference.OfferedMetadata,
                reference.WantedMetadata);
            return latestByPair.TryGetValue(key, out var snapshot) &&
                snapshot.CaptureId == reference.CaptureId &&
                snapshot.ScanId == manifest.ScanId &&
                snapshot.CollectorSessionId == manifest.CollectorSessionId &&
                snapshot.ScanSequence == reference.Sequence &&
                snapshot.CapturedAtUtc >= manifest.StartedAtUtc &&
                snapshot.CapturedAtUtc <= manifest.UpdatedAtUtc &&
                (manifest.TerminalAtUtc == null ||
                    snapshot.CapturedAtUtc <= manifest.TerminalAtUtc) &&
                snapshot.Source == ExchangeCaptureSource.BoundedScanAutomation;
        });
        if (allReferencesCurrent)
        {
            return manifest;
        }

        if (replacingManifest)
        {
            throw new InvalidDataException(
                "The current bounded-scan manifest references a capture that was not persisted.");
        }

        if (manifest.State == "Invalidated")
        {
            return manifest;
        }

        manifest.State = "Invalidated";
        manifest.UpdatedAtUtc = DateTimeOffset.UtcNow;
        manifest.TerminalAtUtc = manifest.UpdatedAtUtc;
        manifest.FailureReason =
            "One or more captures from this scan were superseded by newer latest-pair captures.";
        return manifest;
    }

    private static void ValidateManifestStructure(LatestBoundedScanManifest manifest)
    {
        if (manifest.ScanId == Guid.Empty || manifest.CollectorSessionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(manifest.League) ||
            manifest.StartedAtUtc == default || manifest.UpdatedAtUtc < manifest.StartedAtUtc ||
            manifest.PlannedPairs.Count == 0)
        {
            throw new InvalidDataException("The latest bounded-scan manifest has invalid identity.");
        }

        var plannedBySequence = new Dictionary<int, ScanPairReferenceCapture>();
        var plannedPairs = new HashSet<CurrencyPairKey>();
        for (var index = 0; index < manifest.PlannedPairs.Count; index++)
        {
            var item = manifest.PlannedPairs[index];
            var pair = new CurrencyPairKey(item.OfferedMetadata, item.WantedMetadata);
            if (item.Sequence != index + 1 ||
                string.IsNullOrWhiteSpace(item.OfferedMetadata) ||
                string.IsNullOrWhiteSpace(item.WantedMetadata) ||
                item.OfferedMetadata == item.WantedMetadata ||
                !plannedPairs.Add(pair))
            {
                throw new InvalidDataException(
                    "The latest bounded-scan manifest has an invalid planned pair.");
            }

            plannedBySequence[item.Sequence] = item;
        }

        var persistedSequences = new HashSet<int>();
        var persistedIds = new HashSet<Guid>();
        for (var index = 0; index < manifest.PersistedCaptures.Count; index++)
        {
            var item = manifest.PersistedCaptures[index];
            if (item.CaptureId == Guid.Empty ||
                item.Sequence != index + 1 ||
                !persistedSequences.Add(item.Sequence) ||
                !persistedIds.Add(item.CaptureId) ||
                !plannedBySequence.TryGetValue(item.Sequence, out var planned) ||
                planned.OfferedMetadata != item.OfferedMetadata ||
                planned.WantedMetadata != item.WantedMetadata)
            {
                throw new InvalidDataException(
                    "The latest bounded-scan manifest has an invalid persisted capture reference.");
            }
        }

        if (manifest.State == "Invalidated")
        {
            if (manifest.TerminalAtUtc == null ||
                manifest.TerminalAtUtc < manifest.StartedAtUtc ||
                manifest.TerminalAtUtc > manifest.UpdatedAtUtc ||
                string.IsNullOrWhiteSpace(manifest.FailureReason))
            {
                throw new InvalidDataException("An invalidated scan requires terminal metadata.");
            }

            return;
        }

        if (!Enum.GetNames<BoundedScanState>().Contains(
                manifest.State,
                StringComparer.Ordinal) ||
            !Enum.TryParse<BoundedScanState>(manifest.State, out var state) ||
            !Enum.IsDefined(state) || state == BoundedScanState.Idle)
        {
            throw new InvalidDataException(
                $"Unknown bounded-scan manifest state '{manifest.State}'.");
        }

        var terminal = state is BoundedScanState.Completed or BoundedScanState.Faulted;
        if (terminal != (manifest.TerminalAtUtc != null) ||
            manifest.TerminalAtUtc < manifest.StartedAtUtc ||
            manifest.TerminalAtUtc > manifest.UpdatedAtUtc)
        {
            throw new InvalidDataException(
                "The bounded-scan manifest has inconsistent terminal metadata.");
        }

        if (state == BoundedScanState.Completed &&
            manifest.PersistedCaptures.Count != manifest.PlannedPairs.Count)
        {
            throw new InvalidDataException(
                "A completed bounded scan does not reference every planned capture.");
        }

        if (state == BoundedScanState.Completed &&
            !string.IsNullOrWhiteSpace(manifest.FailureReason))
        {
            throw new InvalidDataException(
                "A completed bounded scan cannot have a failure reason.");
        }

        if (state == BoundedScanState.BetweenPairs &&
            (manifest.PersistedCaptures.Count == 0 ||
                manifest.PersistedCaptures.Count >= manifest.PlannedPairs.Count))
        {
            throw new InvalidDataException(
                "A between-pairs scan has an impossible persisted capture count.");
        }

        if ((state is BoundedScanState.RunningPair or BoundedScanState.AwaitingPersistence) &&
            manifest.PersistedCaptures.Count >= manifest.PlannedPairs.Count)
        {
            throw new InvalidDataException(
                "A running bounded scan cannot have every planned capture persisted.");
        }

        if (state == BoundedScanState.Faulted &&
            string.IsNullOrWhiteSpace(manifest.FailureReason))
        {
            throw new InvalidDataException("A faulted bounded scan requires a failure reason.");
        }

        if (!terminal && !string.IsNullOrWhiteSpace(manifest.FailureReason))
        {
            throw new InvalidDataException("A running bounded scan cannot have a failure reason.");
        }
    }

    private static RationalExchangeRate? RestoreRate(RationalRateCapture? capture)
    {
        if (capture == null)
        {
            return null;
        }

        if (!RationalExchangeRate.TryCreate(capture.RawGet, capture.RawGive, out var rate) ||
            rate!.GetUnits != capture.GetUnits ||
            rate.GiveUnits != capture.GiveUnits ||
            rate.WantedPerOffered != capture.WantedPerOffered)
        {
            throw new InvalidDataException("A persisted rational rate is invalid.");
        }

        return rate;
    }

    private static IReadOnlyList<ExchangeStockLevel> RestoreStock(
        IEnumerable<StockLevelCapture> captures,
        ExchangeStockSide expectedSide)
    {
        var result = new List<ExchangeStockLevel>();
        foreach (var capture in captures)
        {
            if (!Enum.GetNames<ExchangeStockSide>().Contains(
                    capture.Side,
                    StringComparer.Ordinal) ||
                !Enum.TryParse<ExchangeStockSide>(capture.Side, out var side) ||
                side != expectedSide ||
                capture.ListedCount < 0 ||
                !ExchangeStockLevel.TryCreate(
                    side,
                    capture.RawGet,
                    capture.RawGive,
                    capture.ListedCount,
                    out var level))
            {
                throw new InvalidDataException("A persisted stock row is invalid.");
            }

            ValidateRateCapture(capture.RawRate, level!.RawRate);
            ValidateRateCapture(capture.SelectedPairRate, level.SelectedPairRate);
            result.Add(level);
        }

        return result;
    }

    private static void ValidateRateCapture(
        RationalRateCapture? capture,
        RationalExchangeRate? rate)
    {
        if (capture == null && rate == null)
        {
            return;
        }

        if (capture == null || rate == null ||
            capture.RawGet != rate.RawGet ||
            capture.RawGive != rate.RawGive ||
            capture.GetUnits != rate.GetUnits ||
            capture.GiveUnits != rate.GiveUnits ||
            capture.WantedPerOffered != rate.WantedPerOffered)
        {
            throw new InvalidDataException(
                "A persisted derived rate does not match its exact raw ratio.");
        }
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

    private static StockLevelCapture CreateStock(ExchangeStockLevel stock)
    {
        return new StockLevelCapture
        {
            Side = stock.Side.ToString(),
            RawGet = stock.RawGet,
            RawGive = stock.RawGive,
            ListedCount = stock.ListedCount,
            RawRate = CreateRate(stock.RawRate)!,
            SelectedPairRate = CreateRate(stock.SelectedPairRate)!
        };
    }

    private static RationalRateCapture? CreateRate(RationalExchangeRate? rate)
    {
        return rate == null
            ? null
            : new RationalRateCapture
            {
                RawGet = rate.RawGet,
                RawGive = rate.RawGive,
                GetUnits = rate.GetUnits,
                GiveUnits = rate.GiveUnits,
                WantedPerOffered = rate.WantedPerOffered
            };
    }
}

public sealed class RateCaptureExportFile
{
    public int SchemaVersion { get; set; } = 4;
    public DateTimeOffset ExportedAtUtc { get; set; }
    public List<LatestRateCapture> Captures { get; set; } = [];
    public LatestBoundedScanManifest? LatestBoundedScan { get; set; }
    public LatestBoundedScanManifest? LatestCompletedBoundedScan { get; set; }
}

public sealed class LatestBoundedScanManifest
{
    public Guid ScanId { get; set; }
    public Guid CollectorSessionId { get; set; }
    public string League { get; set; } = "";
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? TerminalAtUtc { get; set; }
    public string State { get; set; } = "";
    public string? FailureReason { get; set; }
    public List<ScanPairReferenceCapture> PlannedPairs { get; set; } = [];
    public List<ScanCaptureReferenceCapture> PersistedCaptures { get; set; } = [];
}

public class ScanPairReferenceCapture
{
    public int Sequence { get; set; }
    public string OfferedMetadata { get; set; } = "";
    public string WantedMetadata { get; set; } = "";
}

public sealed class ScanCaptureReferenceCapture : ScanPairReferenceCapture
{
    public Guid CaptureId { get; set; }
}

public class RateCaptureObservation
{
    public Guid CaptureId { get; set; }
    public DateTimeOffset CapturedAtUtc { get; set; }
    public Guid CollectorSessionId { get; set; }
    public Guid? ScanId { get; set; }
    public int? ScanSequence { get; set; }
    public string Source { get; set; } = "";
    public int AreaInstanceId { get; set; }
    public RationalRateCapture? MarketRate { get; set; }
    public RationalRateCapture? TopImmediateRate { get; set; }
    public RationalRateCapture? TopCompetingRate { get; set; }
    public List<StockLevelCapture> WantedItemStock { get; set; } = [];
    public List<StockLevelCapture> OfferedItemStock { get; set; } = [];
}

public sealed class LatestRateCapture : RateCaptureObservation
{
    public string League { get; set; } = "";
    public CurrencyCapture OfferedCurrency { get; set; } = new();
    public CurrencyCapture WantedCurrency { get; set; } = new();
}

public sealed class CurrencyCapture
{
    public string Metadata { get; set; } = "";
    public uint Hash { get; set; }
    public string Name { get; set; } = "";
}

public sealed class RationalRateCapture
{
    public int RawGet { get; set; }
    public int RawGive { get; set; }
    public int GetUnits { get; set; }
    public int GiveUnits { get; set; }
    public decimal WantedPerOffered { get; set; }
}

public sealed class StockLevelCapture
{
    public string Side { get; set; } = "";
    public int RawGet { get; set; }
    public int RawGive { get; set; }
    public int ListedCount { get; set; }
    public RationalRateCapture RawRate { get; set; } = new();
    public RationalRateCapture SelectedPairRate { get; set; } = new();
}

internal sealed class LegacyRateHistoryExportFile
{
    public List<LegacyRateCaptureSeries> Series { get; set; } = [];
}

internal sealed class LegacyLatestRateCaptureExportFile
{
    public List<LatestRateCapture> Captures { get; set; } = [];
}

internal sealed class LegacyRateCaptureSeries
{
    public string League { get; set; } = "";
    public CurrencyCapture OfferedCurrency { get; set; } = new();
    public CurrencyCapture WantedCurrency { get; set; } = new();
    public List<RateCaptureObservation> History { get; set; } = [];
}

internal sealed class LegacyRateCaptureExportFile
{
    public List<LegacySchema1Capture> Captures { get; set; } = [];
}

internal sealed class LegacySchema1Capture
{
    public DateTimeOffset CapturedAtUtc { get; set; }
    public CurrencyCapture OfferedCurrency { get; set; } = new();
    public CurrencyCapture WantedCurrency { get; set; } = new();
    public RationalRateCapture? MarketRate { get; set; }
    public RationalRateCapture? TopImmediateRate { get; set; }
    public RationalRateCapture? TopCompetingRate { get; set; }
    public List<StockLevelCapture> WantedItemStock { get; set; } = [];
    public List<StockLevelCapture> OfferedItemStock { get; set; } = [];
}
