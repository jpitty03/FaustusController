namespace FaustusController;

/// <summary>
/// Everything <see cref="SingleHopExecutionController"/> needs to execute one hop of the
/// selected route: the resolved pair step plus the planned economics from route analysis.
/// The host builds one per hop (<c>TryBuildHopContext</c>) and the multi-hop coordinator
/// feeds them to the executor in order.
/// </summary>
public sealed record HopExecutionContext(
    CurrencyScanPlanStep Step,
    CurrencyCapture OfferedCurrency,
    CurrencyCapture WantedCurrency,
    long Spent,
    long Received,
    int GiveUnitsPerLot,
    int GetUnitsPerLot,
    ExecutionMode Mode,
    string BookSide)
{
    public bool IsResting => Mode == ExecutionMode.RestingLimit;
}

/// <summary>
/// The persisted result of one multi-hop route execution (F5): the per-hop audits plus
/// run totals. Written to FaustusController_multihop-execution-&lt;League&gt;.json (latest
/// run, overwrite).
/// </summary>
public sealed class MultiHopRunFile
{
    public int SchemaVersion { get; set; }
    public DateTimeOffset ExecutedAtUtc { get; set; }
    public string League { get; set; } = "";
    public int RouteRank { get; set; }
    public bool IsCycle { get; set; }
    public int PlannedHopCount { get; set; }
    public int CompletedHopCount { get; set; }
    public bool Success { get; set; }
    public string Outcome { get; set; } = "";
    public long TotalGoldCost { get; set; }
    public long StartAmount { get; set; }
    public long FinalReceived { get; set; }
    public long NetGainUnits { get; set; }
    public List<HopExecutionAuditFile> Hops { get; set; } = [];
}
