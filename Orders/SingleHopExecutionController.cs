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
/// to stage the planned hop, gates placement on an explicit pre-execution rate check
/// (the live staged rate must be no worse than the planned rate), then hands off to
/// <see cref="OrderPlacementController"/> and, once an order is confirmed, produces a
/// post-execution audit comparing actual received units against the plan. The host
/// keeps ticking staging and placement each frame; this coordinator only observes
/// their state and manages the handoff, exactly as placement already observes staging.
/// Any mismatch cancels with a reason instead of retrying, and it never clicks when the
/// rate gate fails.
/// </summary>
public sealed class SingleHopExecutionController
{
    private CurrencyCapture _offeredCurrency = new();
    private CurrencyCapture _wantedCurrency = new();
    private long _plannedSpent;
    private long _plannedReceived;
    private int _plannedGiveUnits;
    private int _plannedGetUnits;
    private double _minWantedPerOffered;
    private long _recomputedSpent;
    private long _recomputedReceived;
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
        long plannedSpent,
        long plannedReceived,
        int plannedGiveUnitsPerLot,
        int plannedGetUnitsPerLot,
        double minWantedPerOffered,
        out string failureReason)
    {
        if (IsRunning)
        {
            return Fail("Single-hop execution is already running.", out failureReason);
        }

        if (plannedSpent <= 0 || plannedReceived <= 0 ||
            plannedGiveUnitsPerLot <= 0 || plannedGetUnitsPerLot <= 0)
        {
            return Fail(
                "Single-hop execution blocked: the selected hop has non-positive economics.",
                out failureReason);
        }

        if (!stagingController.Start(
            gameController,
            step,
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
        _plannedSpent = plannedSpent;
        _plannedReceived = plannedReceived;
        _plannedGiveUnits = plannedGiveUnitsPerLot;
        _plannedGetUnits = plannedGetUnitsPerLot;
        _minWantedPerOffered = minWantedPerOffered;
        _recomputedSpent = plannedSpent;
        _recomputedReceived = plannedReceived;
        _executedGiveUnits = 0;
        _executedGetUnits = 0;
        _ratePreCheckPassed = false;
        _league = gameController.Game.IngameState.ServerData.League;
        _pendingAudit = null;
        State = SingleHopExecutionState.Staging;
        Status = $"Executing hop: staging {_plannedSpent} {_offeredCurrency.Name} -> " +
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

        // Staging has recomputed the amounts to the live rate; the profitability gate
        // decides whether to place. The live staged rate must give at least the caller's
        // floor of wanted units per offered unit (F10: the hop's planned rate; F5: the
        // rate that keeps the whole loop net-positive) — otherwise never click.
        var live = stagingController.StagedImmediateRate;
        if (live == null)
        {
            stagingController.Cancel(
                "Single-hop execution aborted: the staged pair has no live immediate rate.");
            Cancel("Single-hop execution aborted: no live immediate rate to verify against the floor.");
            return;
        }

        _executedGiveUnits = live.GiveUnits;
        _executedGetUnits = live.GetUnits;
        _recomputedSpent = stagingController.OfferedAmount;
        _recomputedReceived = stagingController.WantedAmount;
        _ratePreCheckPassed = live.GetUnits >= _minWantedPerOffered * live.GiveUnits;
        if (!_ratePreCheckPassed)
        {
            stagingController.Cancel(
                "Single-hop execution aborted: the live rate is below the profitability floor.");
            Cancel(
                $"Single-hop execution aborted: live rate {live.GetUnits}:{live.GiveUnits} " +
                $"({(double)live.GetUnits / live.GiveUnits:F4} wanted/offered) is below the floor " +
                $"{_minWantedPerOffered:F4}; no order placed.");
            return;
        }

        if (!placementController.Start(gameController, calibration, out var placeFailure))
        {
            stagingController.Cancel(placeFailure);
            Cancel($"Single-hop execution aborted: placement could not start. {placeFailure}");
            return;
        }

        State = SingleHopExecutionState.Placing;
        Status = $"Rate verified ({live.GetUnits}:{live.GiveUnits} >= planned " +
            $"{_plannedGetUnits}:{_plannedGiveUnits}); placing the order.";
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
        Status = $"Executed {_recomputedSpent} {_offeredCurrency.Name} -> " +
            $"{_pendingAudit.ActualReceived} {_wantedCurrency.Name} " +
            $"(planned {_plannedReceived}, recomputed {_recomputedReceived}; " +
            (_pendingAudit.FullyFilled
                ? "fully filled"
                : $"shortfall {_pendingAudit.ReceivedShortfall}") +
            $"); order {outcome.PlayerOrderId} {outcome.Status}.";
    }

    private HopExecutionAuditFile BuildAudit(PlacedOrderOutcome outcome)
    {
        var offeredSpent = Math.Max(
            0,
            outcome.OriginalOfferedStackSize - outcome.RemainingOfferedStackSize);
        var actualReceived = outcome.OfferedRatioPart > 0
            ? (long)offeredSpent * outcome.WantedRatioPart / outcome.OfferedRatioPart
            : 0;
        // Shortfall is measured against what we actually asked for at the live rate
        // (the recomputed amount), not the stale analysis plan.
        var shortfall = _recomputedReceived - actualReceived;
        return new HopExecutionAuditFile
        {
            SchemaVersion = 1,
            ExecutedAtUtc = DateTimeOffset.UtcNow,
            League = _league,
            OfferedCurrency = _offeredCurrency,
            WantedCurrency = _wantedCurrency,
            PlannedGiveUnitsPerLot = _plannedGiveUnits,
            PlannedGetUnitsPerLot = _plannedGetUnits,
            PlannedSpent = _plannedSpent,
            PlannedReceived = _plannedReceived,
            RecomputedSpent = _recomputedSpent,
            RecomputedReceived = _recomputedReceived,
            ExecutedGiveUnits = _executedGiveUnits,
            ExecutedGetUnits = _executedGetUnits,
            RatePreCheckPassed = _ratePreCheckPassed,
            PlayerOrderId = outcome.PlayerOrderId,
            OrderStatus = outcome.Status,
            GoldCost = outcome.GoldCost,
            OfferedSpent = offeredSpent,
            ActualReceived = actualReceived,
            ReceivedShortfall = shortfall,
            FullyFilled = shortfall <= 0
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
