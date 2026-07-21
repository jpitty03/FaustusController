using ExileCore;

namespace FaustusController;

public enum BoundedScanState
{
    Idle,
    RunningPair,
    AwaitingPersistence,
    BetweenPairs,
    Completed,
    Faulted
}

public sealed class BoundedScanController
{
    private static readonly TimeSpan BetweenPairDelay = TimeSpan.FromMilliseconds(500);

    private readonly SinglePairScanController _pairController = new();
    private readonly HashSet<CurrencyPairKey> _capturedPairs = [];
    private IReadOnlyList<CurrencyScanPlanStep> _steps = [];
    private int _currentStepIndex;
    private DateTimeOffset _nextPairAtUtc;
    private ExchangePairSnapshot? _pendingSnapshot;

    public BoundedScanState State { get; private set; } = BoundedScanState.Idle;
    public string Status { get; private set; } = "Bounded scan automation is disabled by default.";
    public bool IsRunning => State is BoundedScanState.RunningPair or
        BoundedScanState.AwaitingPersistence or
        BoundedScanState.BetweenPairs;
    public Guid ScanId { get; private set; }
    public DateTimeOffset StartedAtUtc { get; private set; }
    public DateTimeOffset? LastCapturedAtUtc { get; private set; }
    public int CapturedCount => _capturedPairs.Count;
    public int PlannedCount => _steps.Count;
    public CurrencyScanPlanStep? CurrentStep =>
        _steps.Count == 0 ? null : _steps[_currentStepIndex];
    public ExchangePairSnapshot? PendingSnapshot =>
        State == BoundedScanState.AwaitingPersistence ? _pendingSnapshot : null;

    public bool Start(
        GameController gameController,
        IReadOnlyList<CurrencyScanPlanStep> collectionSteps,
        CurrencyScanPlanStep firstStep,
        int maximumPairs,
        out string failureReason)
    {
        if (IsRunning)
        {
            return FailStart("A bounded scan is already running.", out failureReason);
        }

        if (maximumPairs <= 0)
        {
            return FailStart("Bounded scan requires at least one pair.", out failureReason);
        }

        if (collectionSteps.Count == 0)
        {
            return FailStart("The initial bounded-scan collection scope is empty.", out failureReason);
        }

        var firstIndex = -1;
        for (var index = 0; index < collectionSteps.Count; index++)
        {
            if (collectionSteps[index].Pair == firstStep.Pair)
            {
                firstIndex = index;
                break;
            }
        }

        if (firstIndex < 0)
        {
            return FailStart(
                "The preview pair is outside the initial currency-to-Chaos/Divine collection scope.",
                out failureReason);
        }

        var pairCount = Math.Min(maximumPairs, collectionSteps.Count);
        var steps = new CurrencyScanPlanStep[pairCount];
        for (var offset = 0; offset < pairCount; offset++)
        {
            steps[offset] = collectionSteps[(firstIndex + offset) % collectionSteps.Count];
        }

        if (steps.Select(step => step.Pair).Distinct().Count() != steps.Length)
        {
            return FailStart("Bounded scan scope contains duplicate directed pairs.", out failureReason);
        }

        _steps = steps;
        _currentStepIndex = 0;
        _capturedPairs.Clear();
        _pendingSnapshot = null;
        ScanId = Guid.NewGuid();
        StartedAtUtc = DateTimeOffset.UtcNow;
        LastCapturedAtUtc = null;
        if (!StartCurrentPair(gameController, out failureReason))
        {
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

        if (!gameController.Window.IsForeground())
        {
            Cancel("Bounded scan cancelled: Path of Exile lost foreground.");
            return;
        }

        if (State == BoundedScanState.BetweenPairs)
        {
            var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
            if (!panel.IsVisible || panel.CurrencyPicker.IsVisible)
            {
                Cancel("Bounded scan cancelled between pairs: expected a visible panel with closed picker.");
                return;
            }

            if (DateTimeOffset.UtcNow < _nextPairAtUtc)
            {
                return;
            }

            if (!StartCurrentPair(gameController, out var startFailure))
            {
                Cancel($"Bounded scan could not start the next pair: {startFailure}");
            }

            return;
        }

        if (State != BoundedScanState.RunningPair)
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
            Cancel($"Bounded scan pair {_currentStepIndex + 1}/{_steps.Count} failed: " +
                _pairController.Status);
            return;
        }

        var snapshot = _pairController.TakeCapturedSnapshot();
        if (snapshot == null)
        {
            Status = $"Scan {ShortScanId}, pair {_currentStepIndex + 1}/{_steps.Count}: " +
                _pairController.Status;
            return;
        }

        var step = _steps[_currentStepIndex];
        if (snapshot.Pair != step.Pair)
        {
            Cancel("Bounded scan rejected a capture that did not match the current directed pair.");
            return;
        }

        if (snapshot.CapturedAtUtc < StartedAtUtc)
        {
            Cancel("Bounded scan rejected a capture older than the current scan.");
            return;
        }

        if (!_capturedPairs.Add(snapshot.Pair))
        {
            Cancel("Bounded scan rejected a duplicate directed-pair capture.");
            return;
        }

        _pendingSnapshot = snapshot;
        State = BoundedScanState.AwaitingPersistence;
        Status = $"Scan {ShortScanId}, pair {_currentStepIndex + 1}/{_steps.Count} captured; " +
            "waiting for JSON persistence before advancing.";
    }

    public bool ConfirmSnapshotPersisted(out string failureReason)
    {
        if (State != BoundedScanState.AwaitingPersistence || _pendingSnapshot == null)
        {
            failureReason = "Bounded scan has no pending snapshot to confirm.";
            return false;
        }

        LastCapturedAtUtc = _pendingSnapshot.CapturedAtUtc;
        _pendingSnapshot = null;
        if (_currentStepIndex + 1 >= _steps.Count)
        {
            State = BoundedScanState.Completed;
            Status = $"Scan {ShortScanId} captured and persisted {_steps.Count} fresh pairs; stopped.";
            failureReason = string.Empty;
            return true;
        }

        _currentStepIndex++;
        _nextPairAtUtc = DateTimeOffset.UtcNow + BetweenPairDelay;
        State = BoundedScanState.BetweenPairs;
        Status = $"Scan {ShortScanId} persisted {_currentStepIndex}/{_steps.Count}; " +
            "waiting 500 ms before the next pair.";
        failureReason = string.Empty;
        return true;
    }

    public void Cancel(string reason)
    {
        if (_pairController.IsRunning)
        {
            _pairController.Cancel(reason);
        }

        State = BoundedScanState.Faulted;
        Status = reason;
    }

    private string ShortScanId => ScanId.ToString("N")[..8];

    private bool StartCurrentPair(
        GameController gameController,
        out string failureReason)
    {
        var step = _steps[_currentStepIndex];
        if (!_pairController.Start(gameController, step, out failureReason))
        {
            State = BoundedScanState.Faulted;
            Status = failureReason;
            return false;
        }

        State = BoundedScanState.RunningPair;
        Status = $"Scan {ShortScanId}, pair {_currentStepIndex + 1}/{_steps.Count}: " +
            $"{step.OfferedCurrency.Name} -> {step.WantedCurrency.Name}.";
        return true;
    }

    private bool FailStart(string reason, out string failureReason)
    {
        State = BoundedScanState.Faulted;
        Status = reason;
        failureReason = reason;
        return false;
    }
}
