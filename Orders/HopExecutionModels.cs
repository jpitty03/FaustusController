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

    // Immutable order intent: Immediate/RestingLimit and the quote provenance (BookSide).
    public string ExecutionMode { get; set; } = ExecutionModes.Immediate;
    public string BookSide { get; set; } = "";

    // Planned economics from the selected route hop.
    public int PlannedGiveUnitsPerLot { get; set; }
    public int PlannedGetUnitsPerLot { get; set; }
    public long PlannedSpent { get; set; }
    public long PlannedReceived { get; set; }

    // Actual amounts the order was staged/typed at (recomputed to the live rate); the
    // executed give/get units below are that live rate. For a resting order any planned
    // spend that did not fit a whole live lot is UncommittedRemainder.
    public long RecomputedSpent { get; set; }
    public long RecomputedReceived { get; set; }
    public long UncommittedRemainder { get; set; }

    // The live market rate at the moment of execution (from the final staged capture),
    // and whether the staged amounts passed the pre-execution rate/slippage gate.
    public int ExecutedGiveUnits { get; set; }
    public int ExecutedGetUnits { get; set; }
    public bool RatePreCheckPassed { get; set; }

    // Actual outcome read from the placed order. Ratio parts are the order's placed
    // offered:wanted ratio (verified equivalent to the staged ratio by cross multiply).
    public int PlayerOrderId { get; set; }
    public string OrderStatus { get; set; } = "";
    public int PlacedOfferedRatioPart { get; set; }
    public int PlacedWantedRatioPart { get; set; }
    public long GoldCost { get; set; }

    // Placement-time snapshot of spent/received. For a Pending order these are only a
    // snapshot — the order is still outstanding and may fill (or be cancelled) later.
    public long OfferedSpent { get; set; }
    public long ActualReceived { get; set; }
    public long ReceivedShortfall { get; set; }

    // Placement semantics. PlacementVerified: a new order matching the staged pair/ratio
    // appeared. CompletedAtPlacement: it was already terminal-completed on verification.
    // OutstandingAtAudit: still Pending (resting orders normally are). FullyFilled is true
    // only for a terminal completed order satisfying the staged wanted amount.
    public bool PlacementVerified { get; set; }
    public bool CompletedAtPlacement { get; set; }
    public bool OutstandingAtAudit { get; set; }
    public bool FullyFilled { get; set; }
}
