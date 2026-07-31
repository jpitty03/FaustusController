using ExileCore;

namespace FaustusController;

public enum MultiHopState
{
    Idle,
    Executing,
    WaitingForFill,
    Collecting,
    Completed,
    Faulted
}

/// <summary>
/// Verified multi-hop route execution (F5). Chains the selected route's hops end to end,
/// sequencing the existing verified controllers: for each hop it drives
/// <see cref="SingleHopExecutionController"/> (stage → pre-rate gate → place → audit), waits
/// for the order to fill, then drives <see cref="OrderCollectionController"/> to sweep the
/// proceeds into the stash so the next hop can offer them (the exchange pulls
/// inventory-then-stash automatically). It owns the executor and collection controllers'
/// ticks during a run — the host suppresses its own F10/F11 ticking while a chain runs — so
/// it consumes each hop's audit itself. Any hop fault, an unfilled order past the timeout, a
/// shortfall, or a gold-budget breach halts the whole chain with a reason; it never retries.
/// </summary>
public sealed class MultiHopExecutionController
{
    private static readonly TimeSpan FillTimeout = TimeSpan.FromSeconds(20);

    private IReadOnlyList<HopExecutionContext> _hops = [];
    private long _goldBudget;
    private double _rateSlippageFraction;
    private long _startAmount;
    private int _index;
    private bool _executorStarted;
    private bool _collectionStarted;
    private HopExecutionAuditFile? _currentAudit;
    private DateTimeOffset _fillDeadlineUtc;
    private long _totalGold;
    private readonly List<HopExecutionAuditFile> _audits = [];

    public MultiHopState State { get; private set; } = MultiHopState.Idle;
    public string Status { get; private set; } = "Multi-hop execution is disabled by default.";
    public bool IsRunning => State is MultiHopState.Executing or
        MultiHopState.WaitingForFill or MultiHopState.Collecting;

    public IReadOnlyList<HopExecutionAuditFile> Audits => _audits;
    public long TotalGoldCost => _totalGold;
    public long StartAmount => _startAmount;
    public int CompletedHopCount => _index;
    public int PlannedHopCount => _hops.Count;
    public long FinalReceived => _audits.Count > 0 ? _audits[^1].ActualReceived : 0;

    public bool Start(
        GameController gameController,
        IReadOnlyList<HopExecutionContext> hops,
        long startAmount,
        long goldBudget,
        double rateSlippageFraction,
        out string failureReason)
    {
        if (IsRunning)
        {
            failureReason = "Multi-hop execution is already running.";
            return false;
        }

        if (hops.Count == 0)
        {
            return Fail("Multi-hop execution blocked: the route has no hops.", out failureReason);
        }

        if (!gameController.Window.IsForeground())
        {
            return Fail(
                "Multi-hop execution blocked: Path of Exile is not foreground.",
                out failureReason);
        }

        _hops = hops;
        _goldBudget = goldBudget;
        _rateSlippageFraction = rateSlippageFraction;
        _startAmount = startAmount;
        _index = 0;
        _executorStarted = false;
        _collectionStarted = false;
        _currentAudit = null;
        _totalGold = 0;
        _audits.Clear();
        State = MultiHopState.Executing;
        Status = $"Multi-hop: executing hop 1/{_hops.Count}.";
        failureReason = string.Empty;
        return true;
    }

    public void Tick(
        GameController gameController,
        SingleHopExecutionController executor,
        OrderStagingController staging,
        OrderPlacementController placement,
        OrderCollectionController collection,
        PickerButtonCalibrationController calibration,
        int cursorSpeed)
    {
        if (!IsRunning)
        {
            return;
        }

        if (!gameController.Window.IsForeground())
        {
            Cancel("Multi-hop cancelled: Path of Exile lost foreground.");
            return;
        }

        switch (State)
        {
            case MultiHopState.Executing:
                TickExecuting(gameController, executor, staging, placement, calibration);
                return;
            case MultiHopState.WaitingForFill:
                TickWaitingForFill(gameController);
                return;
            case MultiHopState.Collecting:
                TickCollecting(gameController, collection, cursorSpeed);
                return;
        }
    }

    public void Cancel(string reason)
    {
        State = MultiHopState.Faulted;
        Status = reason;
    }

    private void TickExecuting(
        GameController gameController,
        SingleHopExecutionController executor,
        OrderStagingController staging,
        OrderPlacementController placement,
        PickerButtonCalibrationController calibration)
    {
        var context = _hops[_index];
        if (!_executorStarted)
        {
            if (!executor.Start(
                gameController,
                staging,
                context.Step,
                context.OfferedCurrency,
                context.WantedCurrency,
                context.Spent,
                context.Received,
                context.GiveUnitsPerLot,
                context.GetUnitsPerLot,
                ComputeHopFloor(),
                out var startFailure))
            {
                Cancel($"Multi-hop halted at hop {_index + 1}: could not start execution. " +
                    startFailure);
                return;
            }

            _executorStarted = true;
            Status = $"Multi-hop: executing hop {_index + 1}/{_hops.Count} " +
                $"({context.Spent} {context.OfferedCurrency.Name} -> " +
                $"{context.Received} {context.WantedCurrency.Name}).";
        }

        executor.Tick(gameController, staging, placement, calibration);
        if (executor.State == SingleHopExecutionState.Faulted)
        {
            Cancel($"Multi-hop halted at hop {_index + 1}: {executor.Status}");
            return;
        }

        if (executor.State != SingleHopExecutionState.Completed)
        {
            return;
        }

        var audit = executor.TakePendingAudit();
        if (audit == null)
        {
            Cancel($"Multi-hop halted at hop {_index + 1}: execution produced no audit.");
            return;
        }

        _currentAudit = audit;
        _audits.Add(audit);
        _totalGold += audit.GoldCost;

        // No shortfall halt here: staging recomputed to the live rate and the executor's
        // profitability floor already vetoed any hop that would break the loop, so a hop
        // that placed is acceptable by construction.
        // An immediate fill is already fully received; only a resting order needs the wait.
        if (audit.FullyFilled)
        {
            BeginCollecting();
            return;
        }

        _fillDeadlineUtc = DateTimeOffset.UtcNow + FillTimeout;
        State = MultiHopState.WaitingForFill;
        Status = $"Multi-hop: hop {_index + 1} placed (order {audit.PlayerOrderId}); " +
            "waiting for it to fill.";
    }

    private void TickWaitingForFill(GameController gameController)
    {
        if (_currentAudit == null)
        {
            Cancel("Multi-hop halted: the current hop audit was lost while waiting for fill.");
            return;
        }

        if (IsOrderCompleted(gameController, _currentAudit.PlayerOrderId))
        {
            BeginCollecting();
            return;
        }

        if (DateTimeOffset.UtcNow >= _fillDeadlineUtc)
        {
            Cancel($"Multi-hop halted at hop {_index + 1}: order {_currentAudit.PlayerOrderId} " +
                $"({_currentAudit.WantedCurrency.Name}) did not fill within " +
                $"{FillTimeout.TotalSeconds:F0}s; collect and continue manually if it fills later.");
        }
    }

    private void BeginCollecting()
    {
        _collectionStarted = false;
        State = MultiHopState.Collecting;
        Status = $"Multi-hop: collecting hop {_index + 1} proceeds to stash.";
    }

    private void TickCollecting(
        GameController gameController,
        OrderCollectionController collection,
        int cursorSpeed)
    {
        if (!_collectionStarted)
        {
            if (!collection.Start(gameController, cursorSpeed, out var startFailure))
            {
                Cancel($"Multi-hop halted at hop {_index + 1}: collection could not start. " +
                    startFailure);
                return;
            }

            _collectionStarted = true;
        }

        collection.Tick(gameController, cursorSpeed);
        if (collection.State == OrderCollectionState.Faulted)
        {
            Cancel($"Multi-hop halted at hop {_index + 1}: {collection.Status}");
            return;
        }

        if (collection.State == OrderCollectionState.Completed)
        {
            AdvanceHop();
        }
    }

    private void AdvanceHop()
    {
        _index++;
        _executorStarted = false;
        _collectionStarted = false;
        _currentAudit = null;
        if (_index >= _hops.Count)
        {
            State = MultiHopState.Completed;
            Status = $"Multi-hop done: {_index}/{_hops.Count} hops executed; " +
                $"gold {_totalGold}; final received {FinalReceived} {LastWantedName()}.";
            return;
        }

        if (_goldBudget > 0 && _totalGold >= _goldBudget)
        {
            Cancel($"Multi-hop halted after hop {_index}: gold budget {_goldBudget} reached " +
                $"(spent {_totalGold}) before hop {_index + 1}.");
            return;
        }

        State = MultiHopState.Executing;
        Status = $"Multi-hop: executing hop {_index + 1}/{_hops.Count}.";
    }

    private string LastWantedName()
    {
        return _audits.Count > 0 ? _audits[^1].WantedCurrency.Name : "";
    }

    // The minimum wanted-per-offered rate hop _index may execute at: its own
    // analysis-planned rate, less the allowed slippage. (The route analyzer's amounts
    // don't telescope — a hop can spend inventory it wasn't handed by the prior hop —
    // so a whole-loop rate-product floor is invalid; each hop is gated against its own
    // planned rate, exactly like F10.)
    private double ComputeHopFloor()
    {
        var hop = _hops[_index];
        if (hop.Spent <= 0)
        {
            return 0;
        }

        var plannedRate = (double)hop.Received / hop.Spent;
        return plannedRate * (1.0 - _rateSlippageFraction);
    }

    private static bool IsOrderCompleted(GameController gameController, int playerOrderId)
    {
        var orders = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel.Orders;
        if (orders == null)
        {
            return false;
        }

        foreach (var order in orders)
        {
            if (order?.PlayerOrderId == playerOrderId)
            {
                return order.IsCompleted;
            }
        }

        return false;
    }

    private bool Fail(string reason, out string failureReason)
    {
        State = MultiHopState.Faulted;
        Status = reason;
        failureReason = reason;
        return false;
    }
}
