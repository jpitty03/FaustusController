using ExileCore;

namespace FaustusController;

public enum SinglePairScanState
{
    Idle,
    EnsurePair,
    OpeningPicker,
    EnteringQuery,
    MovingToOption,
    SelectingOption,
    WaitingForStableRate,
    Completed,
    Faulted
}

public sealed class SinglePairScanController
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RateTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RateSettleDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RatePollDelay = TimeSpan.FromMilliseconds(100);

    private CurrencyScanPlanStep? _step;
    private bool _activeWantedSide;
    private DateTimeOffset _operationDeadlineUtc;
    private DateTimeOffset _rateDeadlineUtc;
    private DateTimeOffset _nextRatePollAtUtc;
    private int _stableRateSamples;
    private int _lastRateGet;
    private int _lastRateGive;
    private ExchangePairSnapshot? _capturedSnapshot;

    public SinglePairScanState State { get; private set; } = SinglePairScanState.Idle;
    public string Status { get; private set; } = "Single-pair automation is disabled by default.";
    public bool IsRunning => State is not SinglePairScanState.Idle and
        not SinglePairScanState.Completed and
        not SinglePairScanState.Faulted;

    public bool Start(
        GameController gameController,
        CurrencyScanPlanStep step,
        out string failureReason)
    {
        if (IsRunning)
        {
            return FailStart("A single-pair scan is already running.", out failureReason);
        }

        if (!gameController.Window.IsForeground())
        {
            return FailStart("Single-pair scan blocked: Path of Exile is not foreground.", out failureReason);
        }

        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (!panel.IsVisible || panel.CurrencyPicker.IsVisible)
        {
            return FailStart(
                "Single-pair scan blocked: panel must be visible with its picker closed.",
                out failureReason);
        }

        _step = step;
        _capturedSnapshot = null;
        _operationDeadlineUtc = DateTimeOffset.UtcNow + OperationTimeout;
        State = SinglePairScanState.EnsurePair;
        Status = $"Starting one pair: {step.OfferedCurrency.Name} -> {step.WantedCurrency.Name}.";
        failureReason = string.Empty;
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

        if (DateTimeOffset.UtcNow >= _operationDeadlineUtc)
        {
            Cancel("Single-pair scan timed out; no retry sent.");
            return;
        }

        if (!gameController.Window.IsForeground())
        {
            Cancel("Single-pair scan cancelled: Path of Exile lost foreground.");
            return;
        }

        switch (State)
        {
            case SinglePairScanState.EnsurePair:
                EnsurePair(gameController, calibration, pickerOpenController, cursorSpeed);
                return;
            case SinglePairScanState.OpeningPicker:
                if (pickerOpenController.State == CalibratedPickerOpenState.Faulted)
                {
                    Cancel($"Single-pair picker open failed: {pickerOpenController.Status}");
                    return;
                }

                if (pickerOpenController.State == CalibratedPickerOpenState.Completed)
                {
                    var target = _activeWantedSide
                        ? _step!.WantedCurrency
                        : _step!.OfferedCurrency;
                    if (!queryController.StartAutomated(
                        target,
                        _activeWantedSide,
                        out var queryFailure))
                    {
                        Cancel($"Single-pair query failed to start: {queryFailure}");
                        return;
                    }

                    State = SinglePairScanState.EnteringQuery;
                    Status = $"Opened {SideName}; entering query for {target.Name}.";
                }

                return;
            case SinglePairScanState.EnteringQuery:
                if (queryController.State == CurrencySearchQueryState.Faulted)
                {
                    Cancel($"Single-pair query failed: {queryController.Status}");
                    return;
                }

                if (queryController.State == CurrencySearchQueryState.Completed)
                {
                    var target = queryController.VerifiedTarget!;
                    if (!tweenController.Start(
                        target.Currency,
                        _activeWantedSide,
                        target.Center,
                        cursorSpeed,
                        out var tweenFailure))
                    {
                        Cancel($"Single-pair option tween failed to start: {tweenFailure}");
                        return;
                    }

                    State = SinglePairScanState.MovingToOption;
                    Status = $"Verified {target.Currency.Name}; tweening to exact option.";
                }

                return;
            case SinglePairScanState.MovingToOption:
                if (tweenController.State == CursorTweenState.Faulted)
                {
                    Cancel($"Single-pair option tween failed: {tweenController.Status}");
                    return;
                }

                if (tweenController.State == CursorTweenState.Completed)
                {
                    if (!selectionController.TryClick(
                        gameController,
                        pickerInspector,
                        catalogue,
                        queryController,
                        tweenController,
                        out var selectionFailure))
                    {
                        Cancel($"Single-pair option click failed: {selectionFailure}");
                        return;
                    }

                    State = SinglePairScanState.SelectingOption;
                    Status = $"Clicked verified {SideName} option; waiting for selection.";
                }

                return;
            case SinglePairScanState.SelectingOption:
                if (selectionController.State == VerifiedOptionSelectionState.Faulted)
                {
                    Cancel($"Single-pair selection failed: {selectionController.Status}");
                    return;
                }

                if (selectionController.State == VerifiedOptionSelectionState.Completed)
                {
                    State = SinglePairScanState.EnsurePair;
                    Status = $"Verified {SideName} selection; checking remaining pair state.";
                }

                return;
            case SinglePairScanState.WaitingForStableRate:
                PollStableRate(gameController, rateCollector);
                return;
        }
    }

    public ExchangePairSnapshot? TakeCapturedSnapshot()
    {
        var snapshot = _capturedSnapshot;
        _capturedSnapshot = null;
        return snapshot;
    }

    public void Cancel(string reason)
    {
        State = SinglePairScanState.Faulted;
        Status = reason;
    }

    private string SideName => _activeWantedSide ? "I want" : "I have";

    private void EnsurePair(
        GameController gameController,
        PickerButtonCalibrationController calibration,
        CalibratedPickerOpenController pickerOpenController,
        int cursorSpeed)
    {
        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (!panel.IsVisible || panel.CurrencyPicker.IsVisible)
        {
            Cancel("Single-pair scan cancelled: expected a visible panel with closed picker.");
            return;
        }

        var wantedMatches = panel.WantedItemType?.Metadata == _step!.WantedCurrency.Metadata;
        var offeredMatches = panel.OfferedItemType?.Metadata == _step.OfferedCurrency.Metadata;
        if (wantedMatches && offeredMatches)
        {
            _stableRateSamples = 0;
            _lastRateGet = 0;
            _lastRateGive = 0;
            _rateDeadlineUtc = DateTimeOffset.UtcNow + RateTimeout;
            _nextRatePollAtUtc = DateTimeOffset.UtcNow + RateSettleDelay;
            State = SinglePairScanState.WaitingForStableRate;
            Status = "Both currencies verified; waiting for three stable market-rate samples.";
            return;
        }

        _activeWantedSide = !wantedMatches;
        if (!pickerOpenController.Start(
            gameController,
            calibration,
            _activeWantedSide,
            cursorSpeed,
            out var openFailure))
        {
            Cancel($"Single-pair picker open failed to start: {openFailure}");
            return;
        }

        State = SinglePairScanState.OpeningPicker;
        Status = $"Opening {SideName} for " +
            $"{(_activeWantedSide ? _step.WantedCurrency.Name : _step.OfferedCurrency.Name)}.";
    }

    private void PollStableRate(
        GameController gameController,
        CurrencyExchangeRateCollector rateCollector)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _nextRatePollAtUtc)
        {
            return;
        }

        if (now >= _rateDeadlineUtc)
        {
            Cancel("Stable-rate capture timed out before three identical samples.");
            return;
        }

        _nextRatePollAtUtc = now + RatePollDelay;
        if (!rateCollector.TryCaptureCurrentPair(
            gameController,
            out var snapshot,
            out var captureFailure))
        {
            Status = $"Waiting for stable rate: {captureFailure}";
            return;
        }

        if (snapshot!.Pair != _step!.Pair)
        {
            Cancel("Stable-rate capture failed: selected pair no longer matches the plan.");
            return;
        }

        if (snapshot.MarketRate == null)
        {
            Status = "Waiting for a positive market rate.";
            return;
        }

        if (snapshot.MarketRate.RawGet == _lastRateGet &&
            snapshot.MarketRate.RawGive == _lastRateGive)
        {
            _stableRateSamples++;
        }
        else
        {
            _lastRateGet = snapshot.MarketRate.RawGet;
            _lastRateGive = snapshot.MarketRate.RawGive;
            _stableRateSamples = 1;
        }

        Status = $"Stable rate {_lastRateGet}:{_lastRateGive}: " +
            $"sample {_stableRateSamples}/3.";
        if (_stableRateSamples >= 3)
        {
            _capturedSnapshot = snapshot;
            State = SinglePairScanState.Completed;
            Status = $"Captured one pair {_step.OfferedCurrency.Name} -> " +
                $"{_step.WantedCurrency.Name} at {_lastRateGet}:{_lastRateGive}; stopped.";
        }
    }

    private bool FailStart(string reason, out string failureReason)
    {
        Cancel(reason);
        failureReason = reason;
        return false;
    }
}
