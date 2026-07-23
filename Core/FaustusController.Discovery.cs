using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using Newtonsoft.Json;
using System.Windows.Forms;
using Vector2 = System.Numerics.Vector2;

namespace FaustusController;

public sealed partial class FaustusController
{
    private ExchangePairSnapshot AttachCaptureContext(
        ExchangePairSnapshot snapshot,
        ExchangeCaptureSource source)
    {
        return snapshot with
        {
            CollectorSessionId = _collectorSessionId,
            Source = source
        };
    }

    private string FormatExportStatus(RateCaptureExportResult result)
    {
        return $"Exported {result.CaptureCount} latest league/pair captures to {_exportPath}";
    }

    private bool RefreshMarketDiscovery()
    {
        if (_catalogue == null)
        {
            _marketDiscoveryDirty = true;
            return true;
        }

        var league = GetCurrentLeague();
        if (string.IsNullOrWhiteSpace(league))
        {
            _marketDiscoveryStatus = "Active-market discovery blocked: current league unavailable.";
            _marketDiscoveryDirty = true;
            _nextMarketDiscoveryRetryUtc = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
            return false;
        }

        try
        {
            _ = EnsureDiscoveryProbeRegistry();
            var durableSnapshots = _exporter.LoadSnapshots(_exportPath);
            SyncPositiveProbeOutcomes(durableSnapshots);
            var result = _marketDiscoveryExporter.Export(
                _catalogue,
                durableSnapshots,
                league,
                TimeSpan.FromMinutes(Settings.MaximumQuoteAgeMinutes.Value),
                _marketDiscoveryPath,
                _discoveryProbeOutcomes.Values,
                _discoveryOverrides.ForceSkipMetadata,
                _discoveryOverrides.ForceIncludeMetadata);
            _marketDiscoveryStatus = $"Discovery catalogue: " +
                $"{result.CatalogueCurrencyCount} currencies; " +
                $"{result.ActiveCurrencyCount} with positive evidence; " +
                $"{result.ObservedActivePairCount}/{result.EligibleProbePairCount} " +
                "currently eligible discovery pairs observed positive.";
            _marketDiscoveryDirty = false;
            _conversionGraphDirty = true;
            _nextConversionGraphRetryUtc = DateTimeOffset.UtcNow;
            _ = RefreshConversionGraph();
            if (!_activeRefreshRun || !_liquidityDiscoveryController.IsRunning)
            {
                _ = TryExportActiveRefreshPlan(GetActiveRefreshRunPlan(), out _);
            }
            return true;
        }
        catch (Exception exception)
        {
            _marketDiscoveryStatus = $"Active-market export failed: {exception.Message}";
            _marketDiscoveryDirty = true;
            _nextMarketDiscoveryRetryUtc = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
            return false;
        }
    }

    private bool RefreshConversionGraph()
    {
        if (_catalogue == null)
        {
            _conversionGraphDirty = true;
            return true;
        }

        var league = GetCurrentLeague();
        if (string.IsNullOrWhiteSpace(league))
        {
            _conversionGraphStatus = "Conversion graph blocked: current league unavailable.";
            _conversionGraphDirty = true;
            _nextConversionGraphRetryUtc = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
            return false;
        }

        try
        {
            if (!EnsureDiscoveryProbeRegistry())
            {
                throw new InvalidOperationException(
                    $"Discovery-probe registry is unavailable for graph coherence validation. " +
                    $"Market discovery status: {_marketDiscoveryStatus}");
            }

            LatestBoundedScanManifest? completedManifest = null;
            var ignoredManifestFailure = "";
            try
            {
                completedManifest = _exporter.LoadLatestCompletedBoundedScan(_exportPath);
            }
            catch (Exception exception)
            {
                ignoredManifestFailure = exception.Message;
            }

            var graphSnapshots = _exporter.LoadSnapshotsForGraph(_exportPath, league);
            var result = _conversionGraphExporter.Export(
                _catalogue,
                graphSnapshots.Snapshots,
                _discoveryProbeOutcomes.Values,
                completedManifest,
                league,
                DateTimeOffset.UtcNow,
                TimeSpan.FromMinutes(Settings.MaximumQuoteAgeMinutes.Value),
                _conversionGraphPath,
                graphSnapshots.ExcludedMalformedCount,
                _discoveryOverrides.ForceSkipMetadata);
            _conversionGraphStatus = $"Conversion graph: {result.VertexCount} vertices; " +
                $"{result.EdgeCount} coherent fresh directed edges; " +
                $"{result.ExcludedStaleCount} stale and " +
                $"{result.ExcludedIncoherentCount} incoherent captures excluded." +
                (ignoredManifestFailure.Length == 0
                    ? ""
                    : $" Invalid completed-manifest provenance was ignored: " +
                        ignoredManifestFailure);
            _conversionGraphDirty = false;
            _lastConversionGraphMaximumAgeMinutes =
                Settings.MaximumQuoteAgeMinutes.Value;
            _nextConversionGraphExpirationUtc =
                result.NextExpirationAtUtc ?? DateTimeOffset.MaxValue;
            return true;
        }
        catch (Exception exception)
        {
            _conversionGraphStatus = $"Conversion graph export failed: {exception.Message}";
            _conversionGraphDirty = true;
            _nextConversionGraphRetryUtc = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
            return false;
        }
    }

    private IReadOnlyList<CurrencyScanPlanStep> GetEligibleLiquidityDiscoverySteps(
        IReadOnlyDictionary<CurrencyPairKey, CurrencyDiscoveryProbeOutcome>? outcomes = null,
        CurrencyDiscoveryOverrides? overrides = null)
    {
        if (_scanPlan == null)
        {
            return [];
        }

        outcomes ??= _discoveryProbeOutcomes;
        overrides ??= _discoveryOverrides;
        var phaseOne = _scanPlan.InitialCollectionSteps
            .Where(step => !IsManuallySkipped(step, overrides))
            .ToArray();
        return phaseOne;
    }

    private LiquidityDiscoveryRunPlan GetLiquidityDiscoveryRunPlan()
    {
        var eligible = GetEligibleLiquidityDiscoverySteps();
        var unresolved = eligible
            .Where(step => !_discoveryProbeOutcomes.TryGetValue(step.Pair, out var outcome) ||
                outcome.Status == CurrencyDiscoveryProbeStatus.Failed)
            .ToArray();
        if (unresolved.Length > 0)
        {
            var phaseOnePairs = _scanPlan!.InitialCollectionSteps
                .Where(step => !IsManuallySkipped(step))
                .Select(step => step.Pair)
                .ToHashSet();
            var label = unresolved.Any(step => phaseOnePairs.Contains(step.Pair))
                ? "Full initial discovery"
                : "Full canonical discovery";
            return new LiquidityDiscoveryRunPlan(label, unresolved, false);
        }

        return new LiquidityDiscoveryRunPlan("Full discovery complete", [], true);
    }

    private LiquidityDiscoveryRunPlan GetActiveRefreshRunPlan()
    {
        var discoveryPlan = GetLiquidityDiscoveryRunPlan();
        if (!discoveryPlan.DiscoveryComplete)
        {
            return new LiquidityDiscoveryRunPlan(
                "Directed active refresh unavailable: F2 discovery is incomplete",
                [],
                false);
        }

        var activeRefresh = GetEligibleLiquidityDiscoverySteps()
            .Where(step => IsManuallyIncluded(step) ||
                _discoveryProbeOutcomes.TryGetValue(step.Pair, out var outcome) &&
                outcome.Status == CurrencyDiscoveryProbeStatus.Active)
            .ToArray();
        return new LiquidityDiscoveryRunPlan(
            "Directed active listing refresh",
            activeRefresh,
            true);
    }

    private bool IsManuallyIncluded(
        CurrencyScanPlanStep step,
        CurrencyDiscoveryOverrides? overrides = null)
    {
        overrides ??= _discoveryOverrides;
        return overrides.ForceIncludeMetadata.Contains(
                step.OfferedCurrency.Metadata) ||
            overrides.ForceIncludeMetadata.Contains(
                step.WantedCurrency.Metadata);
    }

    private bool IsManuallySkipped(
        CurrencyScanPlanStep step,
        CurrencyDiscoveryOverrides? overrides = null)
    {
        overrides ??= _discoveryOverrides;
        return overrides.ForceSkipMetadata.Contains(
                step.OfferedCurrency.Metadata) ||
            overrides.ForceSkipMetadata.Contains(
                step.WantedCurrency.Metadata);
    }

    private readonly record struct LiquidityDiscoveryRunPlan(
        string Label,
        IReadOnlyList<CurrencyScanPlanStep> Steps,
        bool DiscoveryComplete);

    private void SyncPositiveProbeOutcomes(
        IReadOnlyCollection<ExchangePairSnapshot> snapshots)
    {
        if (_catalogue == null || _scanPlan == null ||
            string.IsNullOrWhiteSpace(_discoveryProbeLeague))
        {
            return;
        }

        var eligiblePairs = GetEligibleLiquidityDiscoverySteps()
            .Select(step => step.Pair)
            .ToHashSet();
        var updated = new Dictionary<CurrencyPairKey, CurrencyDiscoveryProbeOutcome>(
            _discoveryProbeOutcomes);
        var changed = false;
        foreach (var snapshot in snapshots)
        {
            if (!string.Equals(
                    snapshot.League,
                    _discoveryProbeLeague,
                    StringComparison.Ordinal) ||
                snapshot.TopImmediateStock == null && snapshot.TopCompetingStock == null ||
                !eligiblePairs.Contains(snapshot.Pair) ||
                updated.TryGetValue(snapshot.Pair, out var existing) &&
                    existing.ObservedAtUtc >= snapshot.CapturedAtUtc)
            {
                continue;
            }

            updated[snapshot.Pair] = new CurrencyDiscoveryProbeOutcome(
                _discoveryProbeLeague,
                snapshot.OfferedCurrency,
                snapshot.WantedCurrency,
                CurrencyDiscoveryProbeStatus.Active,
                snapshot.CapturedAtUtc,
                snapshot.ScanId ?? snapshot.CaptureId,
                snapshot.ScanSequence ?? 1,
                snapshot.CaptureId,
                null);
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        _discoveryProbeStore.Save(
            updated.Values.ToArray(),
            _discoveryProbeLeague,
            _catalogue.Items.Count,
            GetEligibleLiquidityDiscoverySteps(updated).Count,
            _discoveryProbePath);
        _discoveryProbeOutcomes = updated;
    }

    private bool EnsureDiscoveryProbeRegistry()
    {
        if (_catalogue == null)
        {
            return false;
        }

        var league = GetCurrentLeague();
        if (string.IsNullOrWhiteSpace(league))
        {
            return false;
        }

        if (!EnsureDiscoveryOverrides())
        {
            return false;
        }

        if (string.Equals(_discoveryProbeLeague, league, StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            _discoveryProbePath = GetDiscoveryProbePath(league);
            if (_scanPlan == null &&
                !CurrencyScanPlan.TryCreate(_catalogue, out _scanPlan, out _))
            {
                return false;
            }

            var loaded = _discoveryProbeStore.Load(_discoveryProbePath, league);
            var outcomes = loaded.ToDictionary(outcome => outcome.Pair);
            var eligiblePairs = GetEligibleLiquidityDiscoverySteps(outcomes)
                .Select(step => step.Pair)
                .ToHashSet();
            foreach (var snapshot in _exporter
                .LoadSnapshotsForGraph(_exportPath, league)
                .Snapshots)
            {
                if (!string.Equals(snapshot.League, league, StringComparison.Ordinal) ||
                    snapshot.MarketRate == null ||
                    !eligiblePairs.Contains(snapshot.Pair) ||
                    outcomes.ContainsKey(snapshot.Pair))
                {
                    continue;
                }

                outcomes[snapshot.Pair] = new CurrencyDiscoveryProbeOutcome(
                    league,
                    snapshot.OfferedCurrency,
                    snapshot.WantedCurrency,
                    CurrencyDiscoveryProbeStatus.Active,
                    snapshot.CapturedAtUtc,
                    snapshot.ScanId ?? snapshot.CaptureId,
                    snapshot.ScanSequence ?? 1,
                    snapshot.CaptureId,
                    null);
            }

            _discoveryProbeStore.Save(
                outcomes.Values.ToArray(),
                league,
                _catalogue.Items.Count,
                GetEligibleLiquidityDiscoverySteps(outcomes).Count,
                _discoveryProbePath);
            _discoveryProbeOutcomes = outcomes;
            _discoveryProbeLeague = league;
            return true;
        }
        catch (Exception exception)
        {
            _marketDiscoveryStatus = $"Discovery-probe registry failed: {exception.Message}";
            return false;
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        return string.Concat(value.Select(character =>
            invalidCharacters.Contains(character) ? '_' : character));
    }

    private string GetDiscoveryProbePath(string league)
    {
        return Path.Combine(
            ConfigDirectory,
            $"FaustusController_discovery-probes-{SanitizeFileName(league)}.json");
    }

    private string GetDiscoveryOverridePath(string league)
    {
        return Path.Combine(
            ConfigDirectory,
            $"FaustusController_discovery-overrides-{SanitizeFileName(league)}.json");
    }

    private string GetActiveRefreshPlanPath(string league)
    {
        return Path.Combine(
            ConfigDirectory,
            $"FaustusController_refresh-plan-{SanitizeFileName(league)}.json");
    }

    private bool TryExportActiveRefreshPlan(
        LiquidityDiscoveryRunPlan plan,
        out string failureReason)
    {
        if (_catalogue == null)
        {
            failureReason = "the live catalogue is unavailable.";
            _activeRefreshStatus = $"Active refresh plan failed: {failureReason}";
            return false;
        }

        var league = GetCurrentLeague();
        if (string.IsNullOrWhiteSpace(league))
        {
            failureReason = "the current league is unavailable.";
            _activeRefreshStatus = $"Active refresh plan failed: {failureReason}";
            return false;
        }

        try
        {
            _activeRefreshPlanPath = GetActiveRefreshPlanPath(league);
            var result = _activeRefreshPlanExporter.Export(
                _catalogue,
                league,
                plan.DiscoveryComplete,
                plan.DiscoveryComplete ? plan.Steps : [],
                _discoveryProbeOutcomes,
                _discoveryOverrides,
                _activeRefreshPlanPath);
            _activeRefreshStatus = result.Ready
                ? $"Insert refresh plan: {result.PairCount} directed pairs " +
                    $"({result.CanonicalActivePairCount} canonical active, " +
                    $"{result.ForceIncludedPairCount} forced)."
                    : "Insert refresh plan is not ready: complete F2 discovery first.";
            _activeRefreshPlanDirty = false;
            if (!result.Ready)
            {
                failureReason = "F2 discovery still has unresolved directed pairs.";
                return false;
            }

            failureReason = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            failureReason = $"refresh-plan export failed: {exception.Message}";
            _activeRefreshStatus = $"Active refresh plan failed: {exception.Message}";
            _activeRefreshPlanDirty = true;
            _nextActiveRefreshPlanRetryUtc = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
            return false;
        }
    }

    private bool EnsureDiscoveryOverrides(bool forceReload = false)
    {
        if (_catalogue == null)
        {
            return false;
        }

        var league = GetCurrentLeague();
        if (string.IsNullOrWhiteSpace(league))
        {
            return false;
        }

        if (!forceReload && string.Equals(
            _discoveryOverrideLeague,
            league,
            StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            var path = GetDiscoveryOverridePath(league);
            var loaded = _discoveryOverrideStore.LoadOrCreate(path, league, _catalogue);
            var changed = !string.Equals(
                    _discoveryOverrideLeague,
                    league,
                    StringComparison.Ordinal) ||
                !new HashSet<string>(
                    _discoveryOverrides.ForceIncludeMetadata,
                    StringComparer.Ordinal)
                    .SetEquals(loaded.ForceIncludeMetadata) ||
                !new HashSet<string>(
                    _discoveryOverrides.ForceSkipMetadata,
                    StringComparer.Ordinal)
                    .SetEquals(loaded.ForceSkipMetadata);
            if (changed)
            {
                if (_scanPlan != null && string.Equals(
                    _discoveryProbeLeague,
                    league,
                    StringComparison.Ordinal))
                {
                    _discoveryProbeStore.Save(
                        _discoveryProbeOutcomes.Values.ToArray(),
                        league,
                        _catalogue.Items.Count,
                        GetEligibleLiquidityDiscoverySteps(
                            _discoveryProbeOutcomes,
                            loaded).Count,
                        _discoveryProbePath);
                }

                _marketDiscoveryDirty = true;
                _nextMarketDiscoveryRetryUtc = DateTimeOffset.UtcNow;
                _conversionGraphDirty = true;
                _nextConversionGraphRetryUtc = DateTimeOffset.UtcNow;
            }

            _discoveryOverridePath = path;
            _discoveryOverrideLeague = league;
            _discoveryOverrides = loaded;

            return true;
        }
        catch (Exception exception)
        {
            _marketDiscoveryStatus = $"Discovery overrides failed: {exception.Message}";
            return false;
        }
    }

    private bool TryPersistDiscoveryProbe(PendingDiscoveryProbe pendingProbe)
    {
        if (_catalogue == null || _scanPlan == null)
        {
            return false;
        }

        var pendingIsLiveLeague = string.Equals(
            pendingProbe.League,
            GetCurrentLeague(),
            StringComparison.Ordinal);
        if (pendingIsLiveLeague && !EnsureDiscoveryProbeRegistry())
        {
            return false;
        }

        if (pendingProbe.Status == CurrencyDiscoveryProbeStatus.Active)
        {
            if (pendingProbe.Snapshot == null ||
                !StoreAndExportAutomatedSnapshot(
                    pendingProbe.Snapshot,
                    ExchangeCaptureSource.LiquidityDiscoveryAutomation,
                    "Liquidity discovery"))
            {
                return false;
            }
        }

        var outcome = new CurrencyDiscoveryProbeOutcome(
            pendingProbe.League,
            pendingProbe.Step.OfferedCurrency,
            pendingProbe.Step.WantedCurrency,
            pendingProbe.Status,
            pendingProbe.ObservedAtUtc,
            pendingProbe.RunId,
            pendingProbe.RunSequence,
            pendingProbe.Snapshot?.CaptureId,
            pendingProbe.FailureReason);
        var isCurrentRegistry = string.Equals(
            pendingProbe.League,
            _discoveryProbeLeague,
            StringComparison.Ordinal);
        var probePath = GetDiscoveryProbePath(pendingProbe.League);
        var sourceOutcomes = isCurrentRegistry
            ? _discoveryProbeOutcomes
            : _discoveryProbeStore.Load(probePath, pendingProbe.League)
                .ToDictionary(item => item.Pair);
        if (_activeRefreshRun &&
            pendingProbe.Status == CurrencyDiscoveryProbeStatus.Failed &&
            sourceOutcomes.TryGetValue(outcome.Pair, out var priorOutcome) &&
            priorOutcome.Status != CurrencyDiscoveryProbeStatus.Failed)
        {
            _exportStatus = "Active refresh failed without replacing the prior terminal " +
                $"{priorOutcome.Status} outcome for " +
                $"{pendingProbe.Step.OfferedCurrency.Name} -> " +
                $"{pendingProbe.Step.WantedCurrency.Name}.";
            return true;
        }

        var updated = new Dictionary<CurrencyPairKey, CurrencyDiscoveryProbeOutcome>(
            sourceOutcomes)
        {
            [outcome.Pair] = outcome
        };

        try
        {
            _discoveryProbeStore.Save(
                updated.Values.ToArray(),
                pendingProbe.League,
                _catalogue.Items.Count,
                GetEligibleLiquidityDiscoverySteps(updated).Count,
                probePath);
            if (!isCurrentRegistry)
            {
                return true;
            }

            _discoveryProbeOutcomes = updated;
            return pendingProbe.Status == CurrencyDiscoveryProbeStatus.Failed ||
                RefreshMarketDiscovery();
        }
        catch (Exception exception)
        {
            _exportStatus = $"Discovery-probe persistence failed: {exception.Message}";
            return false;
        }
    }

    private void EnsureMarketDiscoveryCatalogue()
    {
        if (_catalogue != null || DateTimeOffset.UtcNow < _nextDiscoveryCatalogueAttemptUtc)
        {
            return;
        }

        _nextDiscoveryCatalogueAttemptUtc = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        if (!_catalogueBuilder.TryBuild(
            GameController,
            out var catalogue,
            out var failureReason))
        {
            _marketDiscoveryStatus = $"Active-market discovery waiting: {failureReason}";
            return;
        }

        _catalogue = catalogue;
        _marketDiscoveryDirty = true;
        _conversionGraphDirty = true;
        _ = EnsureDiscoveryProbeRegistry();
        _ = RefreshMarketDiscovery();
    }
}
