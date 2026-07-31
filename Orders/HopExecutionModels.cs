namespace FaustusController;

/// <summary>
/// The economics of a verified placed order, read back from live game memory once
/// <see cref="OrderPlacementController"/> confirms a new order for the staged pair.
/// Stack-size fields are raw counts; ratio parts are the order's offered:wanted ratio.
/// </summary>
public sealed record PlacedOrderOutcome(
    int PlayerOrderId,
    string Status,
    long GoldCost,
    int OriginalOfferedStackSize,
    int RemainingOfferedStackSize,
    int WantedStackSize,
    int OfferedRatioPart,
    int WantedRatioPart,
    bool IsCompleted,
    bool IsCanceled);

/// <summary>
/// The persisted result of one verified single-hop execution (F10): the planned hop
/// from route analysis, the live rate it actually executed at, and the actual units
/// received versus expected. Written to
/// FaustusController_hop-execution-&lt;League&gt;.json (latest execution, overwrite).
/// </summary>
public sealed class HopExecutionAuditFile
{
    public int SchemaVersion { get; set; }
    public DateTimeOffset ExecutedAtUtc { get; set; }
    public string League { get; set; } = "";
    public CurrencyCapture OfferedCurrency { get; set; } = new();
    public CurrencyCapture WantedCurrency { get; set; } = new();

    // Planned economics from the selected route hop.
    public int PlannedGiveUnitsPerLot { get; set; }
    public int PlannedGetUnitsPerLot { get; set; }
    public long PlannedSpent { get; set; }
    public long PlannedReceived { get; set; }

    // Amounts recomputed to the live market rate at staging time (what we actually
    // asked for); the executed give/get units below are that live rate.
    public long RecomputedSpent { get; set; }
    public long RecomputedReceived { get; set; }

    // The top immediate market rate at the moment of execution (from the final
    // staged capture), and whether it passed the pre-execution rate gate.
    public int ExecutedGiveUnits { get; set; }
    public int ExecutedGetUnits { get; set; }
    public bool RatePreCheckPassed { get; set; }

    // Actual outcome read from the placed order.
    public int PlayerOrderId { get; set; }
    public string OrderStatus { get; set; } = "";
    public long GoldCost { get; set; }
    public long OfferedSpent { get; set; }
    public long ActualReceived { get; set; }
    public long ReceivedShortfall { get; set; }
    public bool FullyFilled { get; set; }
}
