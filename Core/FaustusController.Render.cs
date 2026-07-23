using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using Newtonsoft.Json;
using System.Windows.Forms;
using Vector2 = System.Numerics.Vector2;

namespace FaustusController;

public sealed partial class FaustusController
{
    private void RenderRouteAnalysis(int startY)
    {
        var analysis = _lastRouteAnalysis;
        if (analysis == null || analysis.Routes.Count == 0)
        {
            return;
        }

        var routeIndex = Math.Clamp(_routeDisplayIndex, 0, analysis.Routes.Count - 1);
        var route = analysis.Routes[routeIndex];

        var y = startY;
        var x = 100;
        var now = DateTimeOffset.UtcNow;
        var maxAge = TimeSpan.FromMinutes(analysis.GraphMaximumQuoteAgeMinutes);

        Graphics.DrawText(
            $"=== ROUTE ANALYSIS DRY RUN (Rank {route.Rank}/{analysis.Routes.Count}) ===",
            new Vector2(x, y),
            SharpDX.Color.White);
        y += 20;

        Graphics.DrawText(
            $"{analysis.Request.StartAmount} {route.Hops.FirstOrDefault()?.OfferedCurrency.Name} " +
            $"-> {route.TargetUnits} {route.TargetCurrency.Name} " +
            $"in {route.HopCount} hops | gold: {route.TotalGoldCost} | " +
            $"stranded: {route.StrandedRemainderCurrencyCount} currency",
            new Vector2(x, y),
            SharpDX.Color.Cyan);
        y += 20;

        foreach (var hop in route.Hops)
        {
            var remaining = hop.CapturedAtUtc + maxAge - now;
            var freshnessLabel = remaining > TimeSpan.Zero
                ? $"fresh: {remaining.Minutes}m{remaining.Seconds:D1}s"
                : "STALE";
            var freshnessColor = remaining > TimeSpan.FromMinutes(5)
                ? SharpDX.Color.LimeGreen
                : remaining > TimeSpan.Zero
                    ? SharpDX.Color.Yellow
                    : SharpDX.Color.Red;

            Graphics.DrawText(
                $"  Hop {hop.Sequence}: {hop.OfferedCurrency.Name} -> {hop.WantedCurrency.Name}  [{freshnessLabel}]",
                new Vector2(x, y),
                SharpDX.Color.White);
            y += 20;

            Graphics.DrawText(
                $"    avail: {hop.AvailableBefore} | lots: {hop.Lots} | " +
                $"rate: {hop.GetUnitsPerLot}:{hop.GiveUnitsPerLot} | " +
                $"spent: {hop.Spent} | recv: {hop.Received} | rem: {hop.Remainder}",
                new Vector2(x, y),
                SharpDX.Color.White);
            y += 20;

            var liquidityLabel = analysis.UsesLiquidityLimits
                ? $"fillable: {hop.FillableLots} | capped: {hop.LotsCappedByLiquidity.ToString().ToLowerInvariant()}"
                : "liquidity: disabled";
            Graphics.DrawText(
                $"    {hop.BookSide} | {hop.Coherence} | gold: {hop.GoldCost} | {liquidityLabel}",
                new Vector2(x, y),
                freshnessColor);
            y += 20;
        }

        foreach (var remainder in route.Remainders.Where(r => r.Units > 0))
        {
            Graphics.DrawText(
                $"  Remainder: {remainder.Units} {remainder.Currency.Name}",
                new Vector2(x, y),
                SharpDX.Color.Yellow);
            y += 20;
        }

        var rejectionParts = new List<string>();
        if (analysis.RejectedCycleCount > 0)
        {
            rejectionParts.Add($"cycles {analysis.RejectedCycleCount}");
        }

        if (analysis.RejectedZeroLotCount > 0)
        {
            rejectionParts.Add($"zero-lot {analysis.RejectedZeroLotCount}");
        }

        if (analysis.RejectedOverflowCount > 0)
        {
            rejectionParts.Add($"overflow {analysis.RejectedOverflowCount}");
        }

        if (analysis.RejectedLiquidityLimitCount > 0)
        {
            rejectionParts.Add($"liquidity {analysis.RejectedLiquidityLimitCount}");
        }

        if (analysis.RejectedGoldBudgetCount > 0)
        {
            rejectionParts.Add($"gold {analysis.RejectedGoldBudgetCount}");
        }

        var rejectionLabel = rejectionParts.Count > 0
            ? string.Join(", ", rejectionParts)
            : "none";
        Graphics.DrawText(
            $"Candidates: {analysis.CandidateRouteCount} | expanded: {analysis.ExpandedStateCount} | " +
            $"rejected: {rejectionLabel}",
            new Vector2(x, y),
            SharpDX.Color.Orange);
        y += 20;

        var constraintParts = new List<string>();
        if (analysis.UsesInventoryBalances)
        {
            constraintParts.Add("inventory balances");
        }

        if (analysis.UsesLiquidityLimits)
        {
            constraintParts.Add("liquidity limits");
        }

        if (analysis.UsesGoldCosts)
        {
            constraintParts.Add("gold costs");
        }

        var constraintLabel = constraintParts.Count > 0
            ? string.Join(", ", constraintParts)
            : "none";
        Graphics.DrawText(
            $"Constraints: {constraintLabel} | truncated: {analysis.SearchTruncated.ToString().ToLowerInvariant()} | " +
            $"routes: {analysis.Routes.Count} | PageUp/PageDown to cycle",
            new Vector2(x, y),
            SharpDX.Color.Orange);
    }

    public override void Render()
    {
        var currentLeague = GetCurrentLeague();
        var freshQuoteCount = _rateBook.CountFreshMarketQuotes(
            currentLeague,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(Settings.MaximumQuoteAgeMinutes.Value));
        Graphics.DrawText(_captureStatus, new Vector2(100, 100), SharpDX.Color.White);
        Graphics.DrawText(
            $"Latest captures: {_rateBook.Count}; " +
            $"{freshQuoteCount} fresh latest market quotes in " +
            $"{(string.IsNullOrWhiteSpace(currentLeague) ? "unknown league" : currentLeague)}",
            new Vector2(100, 120),
            SharpDX.Color.White);
        Graphics.DrawText(_exportStatus, new Vector2(100, 140), SharpDX.Color.White);
        Graphics.DrawText(_scanStatus, new Vector2(100, 160), SharpDX.Color.Yellow);
        Graphics.DrawText(_inputStatus, new Vector2(100, 180), SharpDX.Color.Cyan);
        Graphics.DrawText(
            _searchQueryController.Status,
            new Vector2(100, 200),
            SharpDX.Color.Cyan);
        Graphics.DrawText(
            _cursorTweenController.Status,
            new Vector2(100, 220),
            SharpDX.Color.Orange);
        Graphics.DrawText(
            _selectionController.Status,
            new Vector2(100, 240),
            SharpDX.Color.Orange);
        Graphics.DrawText(
            _pickerButtonCalibration.Status,
            new Vector2(100, 260),
            SharpDX.Color.Magenta);
        Graphics.DrawText(
            _pickerOpenController.Status,
            new Vector2(100, 280),
            SharpDX.Color.Magenta);
        Graphics.DrawText(
            _singlePairScanController.Status,
            new Vector2(100, 300),
            SharpDX.Color.LimeGreen);
        Graphics.DrawText(
            _boundedScanController.Status,
            new Vector2(100, 320),
            SharpDX.Color.LimeGreen);
        Graphics.DrawText(
            _marketDiscoveryStatus,
            new Vector2(100, 340),
            SharpDX.Color.Yellow);
        Graphics.DrawText(
            _liquidityDiscoveryController.Status,
            new Vector2(100, 360),
            SharpDX.Color.LimeGreen);
        Graphics.DrawText(
            _conversionGraphStatus,
            new Vector2(100, 380),
            SharpDX.Color.Cyan);
        Graphics.DrawText(
            _activeRefreshStatus,
            new Vector2(100, 400),
            SharpDX.Color.Cyan);
        Graphics.DrawText(
            _routeAnalysisStatus,
            new Vector2(100, 420),
            SharpDX.Color.Cyan);
        Graphics.DrawText(
            _routePlanStatus,
            new Vector2(100, 440),
            SharpDX.Color.Cyan);
        Graphics.DrawText(
            _inventorySyncStatus,
            new Vector2(100, 460),
            SharpDX.Color.Cyan);
        Graphics.DrawText(
            _placedOrdersStatus,
            new Vector2(100, 480),
            SharpDX.Color.Cyan);

        RenderRouteAnalysis(500);

        if (_previewTarget != null)
        {
            Graphics.DrawText(
                $"DRY RUN TARGET: {_previewTarget.Currency.Name}",
                _previewTarget.Center + new Vector2(6, -10),
                SharpDX.Color.Yellow);
        }

        var panel = GameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (panel.IsVisible)
        {
            var panelRectangle = panel.GetClientRectCache;
            if (_pickerButtonCalibration.TryResolve(
                panelRectangle,
                wantedButton: true,
                out var wantedButton))
            {
                Graphics.DrawText(
                    "CALIBRATED I WANT",
                    wantedButton + new Vector2(6, -10),
                    SharpDX.Color.Magenta);
            }

            if (_pickerButtonCalibration.TryResolve(
                panelRectangle,
                wantedButton: false,
                out var offeredButton))
            {
                Graphics.DrawText(
                    "CALIBRATED I HAVE",
                    offeredButton + new Vector2(6, -10),
                    SharpDX.Color.Magenta);
            }
        }
    }
}
