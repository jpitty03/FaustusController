using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using Newtonsoft.Json;
using System.Windows.Forms;
using Vector2 = System.Numerics.Vector2;

namespace FaustusController;

public sealed partial class FaustusController
{
    private void RunRouteAnalysis()
    {
        if (IsAnyAutomationRunning)
        {
            _routeAnalysisStatus = "Route analysis blocked: automated scan is running.";
            return;
        }

        if (_catalogue == null)
        {
            _routeAnalysisStatus = "Route analysis blocked: the live catalogue is unavailable.";
            return;
        }

        var league = GetCurrentLeague();
        if (string.IsNullOrWhiteSpace(league))
        {
            _routeAnalysisStatus = "Route analysis blocked: the current league is unavailable.";
            return;
        }

        try
        {
            if (!RefreshConversionGraph())
            {
                throw new InvalidOperationException(_conversionGraphStatus);
            }

            var sanitizedLeague = SanitizeFileName(league);
            _routeRequestPath = Path.Combine(
                ConfigDirectory,
                $"FaustusController_route-request-{sanitizedLeague}.json");
            _routeAnalysisPath = Path.Combine(
                ConfigDirectory,
                $"FaustusController_route-analysis-{sanitizedLeague}.json");
            var result = _routeAnalyzer.Analyze(
                _catalogue,
                league,
                _conversionGraphPath,
                _routeRequestPath,
                _routeAnalysisPath,
                DateTimeOffset.UtcNow);
            _routeAnalysisStatus = result.RouteFound
                ? $"Route analysis: best output {result.BestTargetUnits}; " +
                    $"{result.CandidateRouteCount} candidates, {result.FreshEdgeCount} fresh edges" +
                    (result.SearchTruncated ? "; SEARCH TRUNCATED." : ".") +
                    FormatRouteConstraints(result)
                : $"Route analysis found no executable whole-unit route across " +
                    $"{result.FreshEdgeCount} fresh edges; {result.ExpiredEdgeCount} expired." +
                    FormatRouteConstraints(result);

            try
            {
                _lastRouteAnalysis = JsonConvert.DeserializeObject<CurrencyRouteAnalysisFile>(
                    File.ReadAllText(_routeAnalysisPath));
                _routeDisplayIndex = 0;
            }
            catch
            {
                _lastRouteAnalysis = null;
            }
        }
        catch (Exception exception)
        {
            _routeAnalysisStatus = $"Route analysis failed: {exception.Message}";
            _lastRouteAnalysis = null;
        }
    }

    private static string FormatRouteConstraints(CurrencyRouteAnalysisResult result)
    {
        var constraints = new List<string>();
        if (result.UsesInventoryBalances)
        {
            constraints.Add("inventory balances");
        }

        if (result.UsesLiquidityLimits)
        {
            constraints.Add("liquidity limits");
        }

        if (result.UsesGoldCosts)
        {
            constraints.Add("gold costs");
        }

        return constraints.Count > 0
            ? $" [{string.Join(", ", constraints)}]"
            : "";
    }

    private void CycleRouteDisplay(bool up)
    {
        if (_lastRouteAnalysis == null || _lastRouteAnalysis.Routes.Count == 0)
        {
            _routeAnalysisStatus = "Route cycling: press Home to run analysis first.";
            return;
        }

        var count = _lastRouteAnalysis.Routes.Count;
        if (count == 1)
        {
            _routeAnalysisStatus = "Route cycling: only one route available.";
            return;
        }

        _routeDisplayIndex = up
            ? (_routeDisplayIndex - 1 + count) % count
            : (_routeDisplayIndex + 1) % count;
        var route = _lastRouteAnalysis.Routes[_routeDisplayIndex];
        _routeAnalysisStatus = $"Route {route.Rank}/{count}: " +
            $"{route.TargetUnits} {route.TargetCurrency.Name} in {route.HopCount} hops " +
            $"(gold {route.TotalGoldCost}, stranded {route.StrandedRemainderCurrencyCount}).";
    }

    private void ExportRouteExecutionPlan()
    {
        if (IsAnyAutomationRunning)
        {
            _routePlanStatus = "Route plan export blocked: automated scan is running.";
            return;
        }

        if (_catalogue == null)
        {
            _routePlanStatus = "Route plan export blocked: the live catalogue is unavailable.";
            return;
        }

        if (_lastRouteAnalysis == null || _lastRouteAnalysis.Routes.Count == 0)
        {
            _routePlanStatus = "Route plan export blocked: press Home to run analysis first.";
            return;
        }

        var league = GetCurrentLeague();
        if (string.IsNullOrWhiteSpace(league))
        {
            _routePlanStatus = "Route plan export blocked: the current league is unavailable.";
            return;
        }

        try
        {
            var sanitizedLeague = SanitizeFileName(league);
            _routePlanPath = Path.Combine(
                ConfigDirectory,
                $"FaustusController_route-execution-{sanitizedLeague}.json");
            var routeIndex = Math.Clamp(
                _routeDisplayIndex,
                0,
                _lastRouteAnalysis.Routes.Count - 1);
            var result = _routePlanExporter.Export(
                _catalogue,
                league,
                _lastRouteAnalysis,
                routeIndex,
                _routePlanPath,
                DateTimeOffset.UtcNow,
                TimeSpan.FromMinutes(Settings.MaximumQuoteAgeMinutes.Value));
            _routePlanStatus = result.Ready
                ? $"Route plan: Rank {_lastRouteAnalysis.Routes[routeIndex].Rank} exported; " +
                    $"{result.StepCount} steps, {result.ExpectedTargetUnits} target units. " +
                    "All edges fresh at export."
                : $"Route plan: Rank {_lastRouteAnalysis.Routes[routeIndex].Rank} exported; " +
                    $"{result.StepCount} steps, {result.ExpectedTargetUnits} target units. " +
                    $"{result.StaleStepCount} STALE step(s) at export.";
        }
        catch (Exception exception)
        {
            _routePlanStatus = $"Route plan export failed: {exception.Message}";
        }
    }

    private string GetCurrentLeague()
    {
        try
        {
            return GameController.Game.IngameState.ServerData.League ?? "";
        }
        catch
        {
            return "";
        }
    }

    private string FormatScanStep(CurrencyScanPlanStep step)
    {
        var collectionIndex = _scanPlan!.InitialCollectionSteps
            .Select((candidate, index) => (candidate, index))
            .First(entry => entry.candidate.Pair == step.Pair)
            .index;
        return $"Initial collection {collectionIndex + 1}/" +
            $"{_scanPlan.InitialCollectionSteps.Count} ({_catalogue!.Items.Count} currencies): " +
            $"{step.OfferedCurrency.Name} -> {step.WantedCurrency.Name}.";
    }

    private static string FormatRatio(RationalExchangeRate? rate)
    {
        return rate == null ? "unavailable" : $"{rate.GetUnits}:{rate.GiveUnits}";
    }
}
