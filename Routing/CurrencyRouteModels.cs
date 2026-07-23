using Newtonsoft.Json;

namespace FaustusController;

public sealed class CurrencyRouteRequestFile
{
    public int SchemaVersion { get; set; }
    public string League { get; set; } = "";
    public CurrencyCapture StartCurrency { get; set; } = new();
    public long StartAmount { get; set; }
    public CurrencyCapture TargetCurrency { get; set; } = new();
    public int MaximumHops { get; set; }
    public int MaximumResults { get; set; }
    public int MaximumExpandedStates { get; set; }
    public List<CurrencyBalance> InventoryBalances { get; set; } = [];
    public bool UseLiquidityLimits { get; set; }
    public long GoldCostPerHop { get; set; }
    public long GoldBudget { get; set; }
}

public sealed class CurrencyRouteAnalysisFile
{
    public int SchemaVersion { get; set; }
    public DateTimeOffset AnalyzedAtUtc { get; set; }
    public string League { get; set; } = "";
    public DateTimeOffset GraphGeneratedAtUtc { get; set; }
    public int GraphMaximumQuoteAgeMinutes { get; set; }
    public CurrencyRouteRequestFile Request { get; set; } = new();
    public int FreshEdgeCount { get; set; }
    public int ExpiredEdgeCount { get; set; }
    public long CandidateRouteCount { get; set; }
    public long ExpandedStateCount { get; set; }
    public long RejectedCycleCount { get; set; }
    public long RejectedZeroLotCount { get; set; }
    public long RejectedOverflowCount { get; set; }
    public long RejectedLiquidityLimitCount { get; set; }
    public long RejectedGoldBudgetCount { get; set; }
    public bool SearchTruncated { get; set; }
    public bool UsesInventoryBalances { get; set; }
    public bool UsesLiquidityLimits { get; set; }
    public bool UsesGoldCosts { get; set; }
    public string Ranking { get; set; } = "";
    public CurrencyRouteCapture? BestRoute { get; set; }
    public List<CurrencyRouteCapture> Routes { get; set; } = [];
}

public sealed class CurrencyRouteCapture
{
    public int Rank { get; set; }
    public CurrencyCapture TargetCurrency { get; set; } = new();
    public long TargetUnits { get; set; }
    public int HopCount { get; set; }
    public int StrandedRemainderCurrencyCount { get; set; }
    public long TotalGoldCost { get; set; }
    public List<CurrencyRouteHopCapture> Hops { get; set; } = [];
    public List<CurrencyRouteRemainderCapture> Remainders { get; set; } = [];
}

public sealed class CurrencyRouteHopCapture
{
    public int Sequence { get; set; }
    public CurrencyCapture OfferedCurrency { get; set; } = new();
    public CurrencyCapture WantedCurrency { get; set; } = new();
    public long AvailableBefore { get; set; }
    public long Lots { get; set; }
    public int GiveUnitsPerLot { get; set; }
    public int GetUnitsPerLot { get; set; }
    public long Spent { get; set; }
    public long Received { get; set; }
    public long Remainder { get; set; }
    public Guid CaptureId { get; set; }
    public DateTimeOffset CapturedAtUtc { get; set; }
    public string BookSide { get; set; } = "";
    public string Coherence { get; set; } = "";
    public long GoldCost { get; set; }
    public int FillableLots { get; set; }
    public bool LotsCappedByLiquidity { get; set; }
}

public sealed class CurrencyRouteRemainderCapture
{
    public CurrencyCapture Currency { get; set; } = new();
    public long Units { get; set; }
}

public sealed record CurrencyBalance(string Metadata, long Units);

public sealed record TransactionCost(long Gold, long SourceCurrencyUnits);

public sealed record ConversionStepResult(
    CurrencyPairKey Pair,
    long InputUnits,
    long SpentUnits,
    long OutputUnits,
    long RemainderUnits,
    long GoldCost);
