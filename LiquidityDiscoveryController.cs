using ExileCore;

namespace FaustusController;

public enum LiquidityDiscoveryState
{
    Idle,
    RunningPair,
    AwaitingPersistence,
    BetweenPairs,
    Completed,
    Faulted
}

public sealed record PendingDiscoveryProbe(
    string League,
    CurrencyScanPlanStep Step,
    CurrencyDiscoveryProbeStatus Status,
    Guid RunId,
    int RunSequence,
    DateTimeOffset ObservedAtUtc,
    ExchangePairSnapshot? Snapshot,
    string? FailureReason);

public sealed class LiquidityDiscoveryController
{
    private static readonly TimeSpan BetweenPairDelay = TimeSpan.FromMilliseconds(500);

    private readonly SinglePairScanController _pairController = new();
    private IReadOnlyList<CurrencyScanPlanStep> _steps = [];
    private int _currentIndex;
    private DateTimeOffset _nextPairAtUtc;
    private PendingDiscoveryProbe? _pendingProbe;
    private bool _stopAfterPending;
    private string? _stopReason;
    private string _league = "";
    private bool _reuseOpenPickerForNextPair;
    private string _runLabel = "Liquidity discovery";

    public LiquidityDiscoveryState State { get; private set; } = LiquidityDiscoveryState.Idle;
    public string Status { get; private set; } =
        "Liquidity discovery automation is disabled by default.";
    public bool IsRunning => State is LiquidityDiscoveryState.RunningPair or
        LiquidityDiscoveryState.AwaitingPersistence or
        LiquidityDiscoveryState.BetweenPairs;
    public Guid RunId { get; private set; }
    public int CompletedCount { get; private set; }
    public int PlannedCount => _steps.Count;
    public PendingDiscoveryProbe? PendingProbe =>
        State == LiquidityDiscoveryState.AwaitingPersistence ? _pendingProbe : null;

    public bool Start(
        GameController gameController,
        IReadOnlyList<CurrencyScanPlanStep> steps,
        out string failureReason,
        string runLabel = "Liquidity discovery")
    {
        if (IsRunning)
        {
            return FailStart("A liquidity-discovery run is already active.", out failureReason);
        }

        if (steps.Count == 0)
        {
            return FailStart(
                "No pairs remain for the requested discovery or active-refresh phase.",
                out failureReason);
        }

        _steps = steps.ToArray();
        _currentIndex = 0;
        _pendingProbe = null;
        _stopAfterPending = false;
        _stopReason = null;
        _runLabel = string.IsNullOrWhiteSpace(runLabel)
            ? "Liquidity discovery"
            : runLabel;
        CompletedCount = 0;
        RunId = Guid.NewGuid();
        _league = gameController.Game.IngameState.ServerData.League;
        if (string.IsNullOrWhiteSpace(_league))
        {
            return FailStart("Liquidity discovery requires a current league.", out failureReason);
        }

        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        _reuseOpenPickerForNextPair = panel.IsVisible && panel.CurrencyPicker.IsVisible;

        if (!StartCurrentPair(gameController, out failureReason))
        {
            State = LiquidityDiscoveryState.Faulted;
            Status = failureReason;
            return false;
        }

        return true;
    }

    public void Tick(
        GameController gameController,
        CurrencyCatalogue catalogue,
        PickerButtonCalibrationController calibration,
        CalibratedPickerOpenController pickerOpenController,
        CurrencyPickerInspector pickerInspector,
        CurrencySearchQueryController queryController,
        CursorTweenController tweenController,
        VerifiedOptionSelectionController selectionController,
        CurrencyExchangeRateCollector rateCollector,
        int cursorSpeed)
    {
        if (!IsRunning)
        {
            return;
        }

        var liveLeague = gameController.Game.IngameState.ServerData.League;
        if (!string.Equals(liveLeague, _league, StringComparison.Ordinal))
        {
            Cancel("Liquidity discovery cancelled: live league changed during the probe.");
            return;
        }

        if (!gameController.Window.IsForeground())
        {
            Cancel("Liquidity discovery cancelled: Path of Exile lost foreground.");
            return;
        }

        if (State == LiquidityDiscoveryState.BetweenPairs)
        {
            var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
            if (!panel.IsVisible || panel.CurrencyPicker.IsVisible &&
                !_reuseOpenPickerForNextPair)
            {
                Cancel(
                    "Liquidity discovery cancelled between probes: an open picker was not approved for unavailable-item continuation.");
                return;
            }

            if (DateTimeOffset.UtcNow < _nextPairAtUtc)
            {
                return;
            }

            if (!StartCurrentPair(gameController, out var failureReason))
            {
                Cancel($"Liquidity discovery could not start its next probe: {failureReason}");
            }

            return;
        }

        if (State != LiquidityDiscoveryState.RunningPair)
        {
            return;
        }

        _pairController.Tick(
            gameController,
            catalogue,
            calibration,
            pickerOpenController,
            pickerInspector,
            queryController,
            tweenController,
            selectionController,
            rateCollector,
            cursorSpeed);

        if (_pairController.State == SinglePairScanState.Faulted)
        {
            var noMarketRate =
                _pairController.FailureKind == SinglePairScanFailureKind.NoMarketRate;
            var unavailable =
                _pairController.FailureKind == SinglePairScanFailureKind.CurrencyUnavailable;
            _pendingProbe = new PendingDiscoveryProbe(
                _league,
                _steps[_currentIndex],
                noMarketRate
                    ? CurrencyDiscoveryProbeStatus.NoMarketRate
                    : unavailable
                        ? CurrencyDiscoveryProbeStatus.Unavailable
                    : CurrencyDiscoveryProbeStatus.Failed,
                RunId,
                _currentIndex + 1,
                DateTimeOffset.UtcNow,
                null,
                noMarketRate || unavailable ? null : _pairController.Status);
            State = LiquidityDiscoveryState.AwaitingPersistence;
            Status = noMarketRate
                ? $"Probe {_currentIndex + 1}/{_steps.Count} confirmed no positive market rate; " +
                    "waiting for outcome persistence."
                : unavailable
                    ? $"Probe {_currentIndex + 1}/{_steps.Count} confirmed the currency is unavailable; " +
                        "waiting for outcome persistence."
                : $"Probe {_currentIndex + 1}/{_steps.Count} failed; " +
                    "waiting for failure persistence.";
            return;
        }

        var snapshot = _pairController.TakeCapturedSnapshot();
        if (snapshot == null)
        {
            Status = $"Discovery probe {_currentIndex + 1}/{_steps.Count}: " +
                _pairController.Status;
            return;
        }

        var contextualSnapshot = snapshot with
        {
            ScanId = RunId,
            ScanSequence = _currentIndex + 1
        };
        if (!string.Equals(contextualSnapshot.League, _league, StringComparison.Ordinal))
        {
            Cancel("Liquidity discovery rejected a capture from a different league.");
            return;
        }

        _pendingProbe = new PendingDiscoveryProbe(
            _league,
            _steps[_currentIndex],
            CurrencyDiscoveryProbeStatus.Active,
            RunId,
            _currentIndex + 1,
            contextualSnapshot.CapturedAtUtc,
            contextualSnapshot,
            null);
        State = LiquidityDiscoveryState.AwaitingPersistence;
        Status = $"Probe {_currentIndex + 1}/{_steps.Count} captured a positive market; " +
            "waiting for rate and outcome persistence.";
    }

    public bool ConfirmProbePersisted(out string failureReason)
    {
        if (State != LiquidityDiscoveryState.AwaitingPersistence || _pendingProbe == null)
        {
            failureReason = "Liquidity discovery has no pending probe to confirm.";
            return false;
        }

        var completedProbe = _pendingProbe;
        _pendingProbe = null;
        CompletedCount++;
        if (completedProbe.Status == CurrencyDiscoveryProbeStatus.Failed || _stopAfterPending)
        {
            State = LiquidityDiscoveryState.Faulted;
            Status = completedProbe.Status == CurrencyDiscoveryProbeStatus.Failed
                ? $"Liquidity discovery stopped after persisted probe failure: " +
                    completedProbe.FailureReason
                : $"Liquidity discovery stopped after persisting its pending outcome: " +
                    _stopReason;
            failureReason = string.Empty;
            return true;
        }

        _reuseOpenPickerForNextPair =
            completedProbe.Status == CurrencyDiscoveryProbeStatus.Unavailable;

        if (_currentIndex + 1 >= _steps.Count)
        {
            State = LiquidityDiscoveryState.Completed;
            Status = $"{_runLabel} persisted {CompletedCount}/{_steps.Count} outcomes; stopped.";
            failureReason = string.Empty;
            return true;
        }

        _currentIndex++;
        _nextPairAtUtc = DateTimeOffset.UtcNow + BetweenPairDelay;
        State = LiquidityDiscoveryState.BetweenPairs;
        Status = $"Liquidity discovery persisted {CompletedCount}/{_steps.Count}; " +
            "waiting 500 ms before the next probe.";
        failureReason = string.Empty;
        return true;
    }

    public void Cancel(string reason)
    {
        if (!IsRunning)
        {
            return;
        }

        _stopAfterPending = true;
        _stopReason = reason;
        _reuseOpenPickerForNextPair = false;
        if (State == LiquidityDiscoveryState.AwaitingPersistence)
        {
            if (_pendingProbe!.Status != CurrencyDiscoveryProbeStatus.Failed)
            {
                Status = "Liquidity discovery cancellation will stop after persisting the " +
                    $"confirmed {_pendingProbe.Status} outcome: {reason}";
                return;
            }

            _pendingProbe = _pendingProbe! with
            {
                Status = CurrencyDiscoveryProbeStatus.Failed,
                ObservedAtUtc = DateTimeOffset.UtcNow,
                Snapshot = null,
                FailureReason = reason
            };
            Status = $"Liquidity discovery cancellation pending failure persistence: {reason}";
            return;
        }

        if (_pairController.IsRunning)
        {
            _pairController.Cancel(reason);
        }

        var step = _steps[_currentIndex];
        _pendingProbe = new PendingDiscoveryProbe(
            _league,
            step,
            CurrencyDiscoveryProbeStatus.Failed,
            RunId,
            _currentIndex + 1,
            DateTimeOffset.UtcNow,
            null,
            reason);
        State = LiquidityDiscoveryState.AwaitingPersistence;
        Status = $"Liquidity discovery cancelled; waiting to persist failure: {reason}";
    }

    public void Block(string reason)
    {
        if (!IsRunning)
        {
            State = LiquidityDiscoveryState.Faulted;
            Status = reason;
        }
    }

    private bool StartCurrentPair(
        GameController gameController,
        out string failureReason)
    {
        var step = _steps[_currentIndex];
        if (!_pairController.Start(
            gameController,
            step,
            out failureReason,
            allowOpenPickerReuse: _reuseOpenPickerForNextPair,
            allowQuotedQueryFallback: true))
        {
            return false;
        }

        _reuseOpenPickerForNextPair = false;

        State = LiquidityDiscoveryState.RunningPair;
        Status = $"{_runLabel} probe {_currentIndex + 1}/{_steps.Count}: " +
            $"{step.OfferedCurrency.Name} -> {step.WantedCurrency.Name}.";
        return true;
    }

    private bool FailStart(string reason, out string failureReason)
    {
        State = LiquidityDiscoveryState.Faulted;
        Status = reason;
        failureReason = reason;
        return false;
    }
}
