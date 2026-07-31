using ExileCore;

namespace FaustusController;

public enum SingleHopExecutionState
{
    Idle,
    Staging,
    Placing,
    Completed,
    Faulted
}

/// <summary>
/// Verified single-hop execution (F10). Orchestrates the two existing verified
/// controllers rather than re-ticking them: it drives <see cref="OrderStagingController"/>
/// to stage the planned hop (immediate or resting), gates placement on an explicit
/// pre-execution rate check (the actual staged amounts must clear the hop's planned rate
/// less the allowed slippage, by exact overflow-safe arithmetic), then hands off to
/// <see cref="OrderPlacementController"/> and, once an order is confirmed, produces a
/// placement audit. For a resting order F10's contract ends at verified placement: it
/// records whether the order is still outstanding (Pending) or already completed, and never
/// waits, collects, or cancels. Any mismatch cancels with a reason instead of retrying, and
/// it never clicks when the rate gate fails.
/// </summary>
public sealed class SingleHopExecutionController
{
    private CurrencyCapture _offeredCurrency = new();
    private CurrencyCapture _wantedCurrency = new();
    private ExecutionMode _mode;
    private string _bookSide = "";
    private long _plannedSpent;
    private long _plannedReceived;
    private int _plannedGiveUnits;
    private int _plannedGetUnits;
    private int _slippagePercent;
    private long _stagedSpent;
    private long _stagedReceived;
    private long _uncommittedRemainder;
    private int _executedGiveUnits;
    private int _executedGetUnits;
    private bool _ratePreCheckPassed;
    private string _league = "";
    private HopExecutionAuditFile? _pendingAudit;

    public SingleHopExecutionState State { get; private set; } = SingleHopExecutionState.Idle;
    public string Status { get; private set; } =
        "Single-hop execution is disabled by default.";
    public bool IsRunning => State is SingleHopExecutionState.Staging or
        SingleHopExecutionState.Placing;

    public bool Start(
        GameController gameController,
        OrderStagingController stagingController,
        CurrencyScanPlanStep step,
        CurrencyCapture offeredCurrency,
        CurrencyCapture wantedCurrency,
        ExecutionMode mode,
        string bookSide,
        long plannedSpent,
        long plannedReceived,
        int plannedGiveUnitsPerLot,
        int plannedGetUnitsPerLot,
        int slippagePercent,
        out string failureReason)
    {
        if (IsRunning)
        {
            return Fail("Single-hop execution is already running.", out failureReason);
        }

        if (plannedSpent <= 0 || plannedReceived <= 0 ||
            plannedGiveUnitsPerLot <= 0 || plannedGetUnitsPerLot <= 0 ||
            slippagePercent is < 0 or > 100)
        {
            return Fail(
                "Single-hop execution blocked: the selected hop has non-positive economics.",
                out failureReason);
        }

        if (!stagingController.Start(
            gameController,
            step,
            mode,
            plannedSpent,
            plannedReceived,
            out var stagingFailure))
        {
            return Fail(
                $"Single-hop execution blocked: staging could not start. {stagingFailure}",
                out failureReason);
        }

        _offeredCurrency = offeredCurrency;
        _wantedCurrency = wantedCurrency;
        _mode = mode;
        _bookSide = bookSide;
        _plannedSpent = plannedSpent;
        _plannedReceived = plannedReceived;
        _plannedGiveUnits = plannedGiveUnitsPerLot;
        _plannedGetUnits = plannedGetUnitsPerLot;
        _slippagePercent = slippagePercent;
        _stagedSpent = plannedSpent;
        _stagedReceived = plannedReceived;
        _uncommittedRemainder = 0;
        _executedGiveUnits = 0;
        _executedGetUnits = 0;
        _ratePreCheckPassed = false;
        _league = gameController.Game.IngameState.ServerData.League;
        _pendingAudit = null;
        State = SingleHopExecutionState.Staging;
        Status = $"Executing {ModeLabel} hop: staging {_plannedSpent} {_offeredCurrency.Name} -> " +
            $"{_plannedReceived} {_wantedCurrency.Name}.";
        failureReason = string.Empty;
        return true;
    }

    public void Tick(
        GameController gameController,
        OrderStagingController stagingController,
        OrderPlacementController placementController,
        PickerButtonCalibrationController calibration)
    {
        switch (State)
        {
            case SingleHopExecutionState.Staging:
                TickStaging(gameController, stagingController, placementController, calibration);
                return;
            case SingleHopExecutionState.Placing:
                TickPlacing(placementController);
                return;
        }
    }

    /// <summary>
    /// Returns the finished execution's audit exactly once so the host can persist it.
    /// </summary>
    public HopExecutionAuditFile? TakePendingAudit()
    {
        var audit = _pendingAudit;
        _pendingAudit = null;
        return audit;
    }

    public void Cancel(string reason)
    {
        State = SingleHopExecutionState.Faulted;
        Status = reason;
    }

    private string ModeLabel => _mode == ExecutionMode.RestingLimit ? "resting-limit" : "immediate";

    private void TickStaging(
        GameController gameController,
        OrderStagingController stagingController,
        OrderPlacementController placementController,
        PickerButtonCalibrationController calibration)
    {
        if (stagingController.State == OrderStagingState.Faulted)
        {
            Cancel($"Single-hop execution aborted during staging: {stagingController.Status}");
            return;
        }

        if (stagingController.State != OrderStagingState.Staged)
        {
            Status = $"Executing hop (staging): {stagingController.Status}";
            return;
        }

        // Staging sized the actual amounts to the live rate; the profitability gate now decides
        // whether to place. Gate the REAL staged amounts (not just the book head) against the
        // hop's planned rate less the allowed slippage, so floor rounding can never place a
        // ratio below the advertised floor.
        var live = stagingController.StagedRate;
        if (live == null)
        {
            stagingController.Cancel(
                "Single-hop execution aborted: the staged pair has no live rate.");
            Cancel("Single-hop execution aborted: no live rate to verify against the floor.");
            return;
        }

        _executedGiveUnits = live.GiveUnits;
        _executedGetUnits = live.GetUnits;
        _stagedSpent = stagingController.OfferedAmount;
        _stagedReceived = stagingController.WantedAmount;
        _uncommittedRemainder = stagingController.UncommittedRemainder;
        _ratePreCheckPassed = OrderExecutionMath.PassesSlippageFloor(
            _stagedSpent,
            _stagedReceived,
            _plannedGetUnits,
            _plannedGiveUnits,
            _slippagePercent);
        if (!_ratePreCheckPassed)
        {
            stagingController.Cancel(
                "Single-hop execution aborted: the staged ratio is below the profitability floor.");
            Cancel(
                $"Single-hop execution aborted: staged {_stagedReceived}:{_stagedSpent} is below the " +
                $"planned {_plannedGetUnits}:{_plannedGiveUnits} floor (slippage {_slippagePercent}%); " +
                "no order placed.");
            return;
        }

        if (!placementController.Start(gameController, calibration, out var placeFailure))
        {
            stagingController.Cancel(placeFailure);
            Cancel($"Single-hop execution aborted: placement could not start. {placeFailure}");
            return;
        }

        State = SingleHopExecutionState.Placing;
        Status = $"Rate verified (staged {_stagedReceived}:{_stagedSpent} clears planned " +
            $"{_plannedGetUnits}:{_plannedGiveUnits}); placing the {ModeLabel} order.";
    }

    private void TickPlacing(OrderPlacementController placementController)
    {
        if (placementController.State == OrderPlacementState.Faulted)
        {
            Cancel($"Single-hop execution aborted during placement: {placementController.Status}");
            return;
        }

        if (placementController.State != OrderPlacementState.Completed)
        {
            Status = $"Executing hop (placing): {placementController.Status}";
            return;
        }

        var outcome = placementController.Outcome;
        if (outcome == null)
        {
            Cancel("Single-hop execution completed but the placed-order outcome was unavailable.");
            return;
        }

        _pendingAudit = BuildAudit(outcome);
        State = SingleHopExecutionState.Completed;
        if (_pendingAudit.OutstandingAtAudit)
        {
            Status = $"Placed and OUTSTANDING ({ModeLabel}): {_stagedSpent} {_offeredCurrency.Name} -> " +
                $"{_stagedReceived} {_wantedCurrency.Name}; order {outcome.PlayerOrderId} is Pending. " +
                "Manage it manually (collect/cancel).";
        }
        else
        {
            Status = $"Completed at placement ({ModeLabel}): {_stagedSpent} {_offeredCurrency.Name} -> " +
                $"{_pendingAudit.ActualReceived} {_wantedCurrency.Name} " +
                (_pendingAudit.FullyFilled
                    ? "fully filled"
                    : $"shortfall {_pendingAudit.ReceivedShortfall}") +
                $"; order {outcome.PlayerOrderId} {outcome.Status}.";
        }
    }

    private HopExecutionAuditFile BuildAudit(PlacedOrderOutcome outcome)
    {
        var offeredSpent = Math.Max(
            0,
            outcome.OriginalOfferedStackSize - outcome.RemainingOfferedStackSize);
        var actualReceived = outcome.OfferedRatioPart > 0
            ? (long)offeredSpent * outcome.WantedRatioPart / outcome.OfferedRatioPart
            : 0;
        // A pending (resting or unfilled) order is still outstanding regardless of any
        // partial fill; only a terminal completed order that satisfies the staged wanted
        // amount is FullyFilled. Shortfall is measured against the staged (live-recomputed)
        // wanted amount, not the stale analysis plan.
        var outstanding = !outcome.IsCompleted && !outcome.IsCanceled;
        var shortfall = _stagedReceived - actualReceived;
        var fullyFilled = outcome.IsCompleted && shortfall <= 0;
        return new HopExecutionAuditFile
        {
            SchemaVersion = 2,
            ExecutedAtUtc = DateTimeOffset.UtcNow,
            League = _league,
            OfferedCurrency = _offeredCurrency,
            WantedCurrency = _wantedCurrency,
            ExecutionMode = ExecutionModes.ToPersisted(_mode),
            BookSide = _bookSide,
            PlannedGiveUnitsPerLot = _plannedGiveUnits,
            PlannedGetUnitsPerLot = _plannedGetUnits,
            PlannedSpent = _plannedSpent,
            PlannedReceived = _plannedReceived,
            RecomputedSpent = _stagedSpent,
            RecomputedReceived = _stagedReceived,
            UncommittedRemainder = _uncommittedRemainder,
            ExecutedGiveUnits = _executedGiveUnits,
            ExecutedGetUnits = _executedGetUnits,
            RatePreCheckPassed = _ratePreCheckPassed,
            PlayerOrderId = outcome.PlayerOrderId,
            OrderStatus = outcome.Status,
            PlacedOfferedRatioPart = outcome.OfferedRatioPart,
            PlacedWantedRatioPart = outcome.WantedRatioPart,
            GoldCost = outcome.GoldCost,
            OfferedSpent = offeredSpent,
            ActualReceived = actualReceived,
            ReceivedShortfall = shortfall,
            PlacementVerified = true,
            CompletedAtPlacement = outcome.IsCompleted,
            OutstandingAtAudit = outstanding,
            FullyFilled = fullyFilled
        };
    }

    private bool Fail(string reason, out string failureReason)
    {
        State = SingleHopExecutionState.Faulted;
        Status = reason;
        failureReason = reason;
        return false;
    }
}
