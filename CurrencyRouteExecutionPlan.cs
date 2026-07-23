using Newtonsoft.Json;

namespace FaustusController;

public readonly record struct RouteExecutionPlanResult(
    bool Ready,
    int StepCount,
    long ExpectedTargetUnits,
    int StaleStepCount);

public sealed class CurrencyRouteExecutionPlanExporter
{
    private const int CurrentSchemaVersion = 1;

    public RouteExecutionPlanResult Export(
        CurrencyCatalogue catalogue,
        string league,
        CurrencyRouteAnalysisFile analysis,
        int routeIndex,
        string outputPath,
        DateTimeOffset exportedAtUtc,
        TimeSpan maximumQuoteAge)
    {
        if (string.IsNullOrWhiteSpace(league))
        {
            throw new ArgumentException(
                "A route execution plan requires a league.",
                nameof(league));
        }

        if (routeIndex < 0 || routeIndex >= analysis.Routes.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(routeIndex),
                "Route index is out of range for the analysis.");
        }

        var route = analysis.Routes[routeIndex];
        var steps = new List<RouteExecutionStepCapture>();
        var staleCount = 0;

        foreach (var hop in route.Hops)
        {
            var ageAtExport = exportedAtUtc - hop.CapturedAtUtc;
            var freshAtExport = ageAtExport >= TimeSpan.Zero &&
                ageAtExport <= maximumQuoteAge;
            if (!freshAtExport)
            {
                staleCount++;
            }

            steps.Add(new RouteExecutionStepCapture
            {
                Sequence = hop.Sequence,
                OfferedCurrency = hop.OfferedCurrency,
                WantedCurrency = hop.WantedCurrency,
                CaptureId = hop.CaptureId,
                CapturedAtUtc = hop.CapturedAtUtc,
                AgeSecondsAtExport = (long)ageAtExport.TotalSeconds,
                FreshAtExport = freshAtExport,
                BookSide = hop.BookSide,
                Coherence = hop.Coherence,
                GiveUnitsPerLot = hop.GiveUnitsPerLot,
                GetUnitsPerLot = hop.GetUnitsPerLot,
                ExpectedAvailableBefore = hop.AvailableBefore,
                ExpectedLots = hop.Lots,
                ExpectedSpent = hop.Spent,
                ExpectedReceived = hop.Received,
                ExpectedRemainder = hop.Remainder,
                GoldCost = hop.GoldCost,
                FillableLots = hop.FillableLots,
                LotsCappedByLiquidity = hop.LotsCappedByLiquidity
            });
        }

        var file = new RouteExecutionPlanFile
        {
            SchemaVersion = CurrentSchemaVersion,
            ExportedAtUtc = exportedAtUtc,
            League = league,
            SourceRouteRank = route.Rank,
            SourceRouteTotalCount = analysis.Routes.Count,
            Ready = staleCount == 0,
            FreshAtExport = staleCount == 0,
            StaleStepCount = staleCount,
            StartCurrency = analysis.Request.StartCurrency,
            StartAmount = analysis.Request.StartAmount,
            TargetCurrency = route.TargetCurrency,
            ExpectedTargetUnits = route.TargetUnits,
            TotalGoldCost = route.TotalGoldCost,
            HopCount = route.HopCount,
            UsesInventoryBalances = analysis.UsesInventoryBalances,
            UsesLiquidityLimits = analysis.UsesLiquidityLimits,
            UsesGoldCosts = analysis.UsesGoldCosts,
            Steps = steps,
            Remainders = route.Remainders
                .Where(remainder => remainder.Units > 0)
                .Select(remainder => new RouteExecutionRemainderCapture
                {
                    Currency = remainder.Currency,
                    Units = remainder.Units
                })
                .ToList()
        };

        Validate(file, catalogue);

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

        return new RouteExecutionPlanResult(
            file.Ready,
            file.Steps.Count,
            file.ExpectedTargetUnits,
            file.StaleStepCount);
    }

    private static void Validate(
        RouteExecutionPlanFile file,
        CurrencyCatalogue catalogue)
    {
        if (file.SchemaVersion != CurrentSchemaVersion ||
            file.ExportedAtUtc == default ||
            string.IsNullOrWhiteSpace(file.League) ||
            file.SourceRouteRank < 1 ||
            file.SourceRouteTotalCount < 1 ||
            file.SourceRouteRank > file.SourceRouteTotalCount ||
            file.StartAmount <= 0 ||
            file.ExpectedTargetUnits <= 0 ||
            file.HopCount < 1 ||
            file.Steps.Count != file.HopCount ||
            file.StaleStepCount < 0 ||
            file.StaleStepCount > file.Steps.Count ||
            file.Ready != (file.StaleStepCount == 0) ||
            file.FreshAtExport != (file.StaleStepCount == 0))
        {
            throw new InvalidDataException(
                "Route execution plan root metadata is invalid.");
        }

        var visitedCurrencies = new HashSet<string>(StringComparer.Ordinal);
        var previousWanted = file.StartCurrency.Metadata;
        visitedCurrencies.Add(previousWanted);

        for (var i = 0; i < file.Steps.Count; i++)
        {
            var step = file.Steps[i];
            if (step.Sequence != i + 1 ||
                string.IsNullOrWhiteSpace(step.OfferedCurrency.Metadata) ||
                string.IsNullOrWhiteSpace(step.WantedCurrency.Metadata) ||
                step.OfferedCurrency.Metadata != previousWanted ||
                !catalogue.TryGetByMetadata(step.OfferedCurrency.Metadata, out _) ||
                !catalogue.TryGetByMetadata(step.WantedCurrency.Metadata, out _) ||
                step.OfferedCurrency.Metadata == step.WantedCurrency.Metadata ||
                !visitedCurrencies.Add(step.WantedCurrency.Metadata) ||
                step.GiveUnitsPerLot <= 0 || step.GetUnitsPerLot <= 0 ||
                step.ExpectedLots <= 0 || step.ExpectedSpent <= 0 ||
                step.ExpectedReceived <= 0 || step.ExpectedRemainder < 0 ||
                step.GoldCost < 0 || step.FillableLots < 0 ||
                step.BookSide is not "ImmediateBook" and not "CompetingBook" ||
                step.Coherence is not "ActiveDiscoveryProbe" and
                    not "CompletedBoundedScan" ||
                step.ExpectedSpent != (long)step.ExpectedLots * step.GiveUnitsPerLot ||
                step.ExpectedReceived != (long)step.ExpectedLots * step.GetUnitsPerLot)
            {
                throw new InvalidDataException(
                    $"Route execution plan step {step.Sequence} is invalid.");
            }

            previousWanted = step.WantedCurrency.Metadata;
        }
    }
}

public sealed class RouteExecutionPlanFile
{
    public int SchemaVersion { get; set; }
    public DateTimeOffset ExportedAtUtc { get; set; }
    public string League { get; set; } = "";
    public int SourceRouteRank { get; set; }
    public int SourceRouteTotalCount { get; set; }
    public bool Ready { get; set; }
    public bool FreshAtExport { get; set; }
    public int StaleStepCount { get; set; }
    public CurrencyCapture StartCurrency { get; set; } = new();
    public long StartAmount { get; set; }
    public CurrencyCapture TargetCurrency { get; set; } = new();
    public long ExpectedTargetUnits { get; set; }
    public long TotalGoldCost { get; set; }
    public int HopCount { get; set; }
    public bool UsesInventoryBalances { get; set; }
    public bool UsesLiquidityLimits { get; set; }
    public bool UsesGoldCosts { get; set; }
    public List<RouteExecutionStepCapture> Steps { get; set; } = [];
    public List<RouteExecutionRemainderCapture> Remainders { get; set; } = [];
}

public sealed class RouteExecutionStepCapture
{
    public int Sequence { get; set; }
    public CurrencyCapture OfferedCurrency { get; set; } = new();
    public CurrencyCapture WantedCurrency { get; set; } = new();
    public Guid CaptureId { get; set; }
    public DateTimeOffset CapturedAtUtc { get; set; }
    public long AgeSecondsAtExport { get; set; }
    public bool FreshAtExport { get; set; }
    public string BookSide { get; set; } = "";
    public string Coherence { get; set; } = "";
    public int GiveUnitsPerLot { get; set; }
    public int GetUnitsPerLot { get; set; }
    public long ExpectedAvailableBefore { get; set; }
    public long ExpectedLots { get; set; }
    public long ExpectedSpent { get; set; }
    public long ExpectedReceived { get; set; }
    public long ExpectedRemainder { get; set; }
    public long GoldCost { get; set; }
    public int FillableLots { get; set; }
    public bool LotsCappedByLiquidity { get; set; }
}

public sealed class RouteExecutionRemainderCapture
{
    public CurrencyCapture Currency { get; set; } = new();
    public long Units { get; set; }
}
