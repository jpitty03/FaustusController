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

public enum SinglePairScanFailureKind
{
    None,
    NoMarketRate,
    CurrencyUnavailable,
    Automation
}

public sealed class SinglePairScanController
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RateTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RateSettleDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RatePollDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaximumNoMarketSampleGap = TimeSpan.FromMilliseconds(300);

    private CurrencyScanPlanStep? _step;
    private bool _activeWantedSide;
    private DateTimeOffset _operationDeadlineUtc;
    private DateTimeOffset _rateDeadlineUtc;
    private DateTimeOffset _nextRatePollAtUtc;
    private bool _allowOpenPickerReuse;
    private bool _allowQuotedQueryFallback;
    private int _stableRateSamples;
    private int _lastRateGet;
    private int _lastRateGive;
    private ExchangePairSnapshot? _capturedSnapshot;
    private bool _sawPositiveMarketRate;
    private int _readableNoMarketSamples;
    private bool _rateReadFailed;
    private DateTimeOffset _lastReadableNoMarketAtUtc;
    private bool _quotedQueryRetryAttempted;

    public SinglePairScanState State { get; private set; } = SinglePairScanState.Idle;
    public string Status { get; private set; } = "Single-pair automation is disabled by default.";
    public bool IsRunning => State is not SinglePairScanState.Idle and
        not SinglePairScanState.Completed and
        not SinglePairScanState.Faulted;
    public SinglePairScanFailureKind FailureKind { get; private set; }

    public bool Start(
        GameController gameController,
        CurrencyScanPlanStep step,
        out string failureReason,
        bool allowOpenPickerReuse = false,
        bool allowQuotedQueryFallback = false)
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
        if (!panel.IsVisible || panel.CurrencyPicker.IsVisible && !allowOpenPickerReuse)
        {
            return FailStart(
                "Single-pair scan blocked: panel must be visible and any open picker must be an approved F2 continuation.",
                out failureReason);
        }

        _step = step;
        _capturedSnapshot = null;
        _sawPositiveMarketRate = false;
        _readableNoMarketSamples = 0;
        _rateReadFailed = false;
        _lastReadableNoMarketAtUtc = default;
        _quotedQueryRetryAttempted = false;
        _allowOpenPickerReuse = allowOpenPickerReuse;
        _allowQuotedQueryFallback = allowQuotedQueryFallback;
        FailureKind = SinglePairScanFailureKind.None;
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
                EnsurePair(
                    gameController,
                    catalogue,
                    calibration,
                    pickerOpenController,
                    queryController,
                    cursorSpeed);
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
                    _quotedQueryRetryAttempted = false;
                    if (!queryController.StartAutomated(
                        target,
                        _activeWantedSide,
                        catalogue,
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
                    if (queryController.FailureKind ==
                        CurrencySearchQueryFailureKind.NoExactMetadataMatch)
                    {
                        if (_allowQuotedQueryFallback && !_quotedQueryRetryAttempted)
                        {
                            StartQuotedQuery(
                                queryController,
                                catalogue,
                                "Normal query did not produce a visible exact metadata match");
                            return;
                        }

                        Cancel(
                            $"{queryController.TargetCurrency!.Name} is unavailable in the picker.",
                            SinglePairScanFailureKind.CurrencyUnavailable);
                        return;
                    }

                    Cancel($"Single-pair query failed: {queryController.Status}");
                    return;
                }

                if (queryController.State == CurrencySearchQueryState.Completed)
                {
                    var target = queryController.VerifiedTarget!;
                    if (!IsTargetInsidePicker(gameController, target))
                    {
                        HandleTargetOutsidePicker(queryController, catalogue);
                        return;
                    }

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
                    if (tweenController.FailedBecauseTargetIsOutsidePicker)
                    {
                        HandleTargetOutsidePicker(queryController, catalogue);
                        return;
                    }

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

    public void Cancel(
        string reason,
        SinglePairScanFailureKind failureKind = SinglePairScanFailureKind.Automation)
    {
        State = SinglePairScanState.Faulted;
        Status = reason;
        FailureKind = failureKind;
    }

    private string SideName => _activeWantedSide ? "I want" : "I have";

    private bool IsTargetInsidePicker(
        GameController gameController,
        CurrencyPickerOptionTarget target)
    {
        var rectangle = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel
            .CurrencyPicker.GetClientRectCache;
        return target.Center.X >= rectangle.X &&
            target.Center.X <= rectangle.X + rectangle.Width &&
            target.Center.Y >= rectangle.Y &&
            target.Center.Y <= rectangle.Y + rectangle.Height;
    }

    private void HandleTargetOutsidePicker(
        CurrencySearchQueryController queryController,
        CurrencyCatalogue catalogue)
    {
        const string failure =
            "target center is outside the picker rectangle";
        if (!_allowQuotedQueryFallback)
        {
            Cancel($"Single-pair option tween failed: {failure}.");
            return;
        }

        if (!_quotedQueryRetryAttempted)
        {
            StartQuotedQuery(
                queryController,
                catalogue,
                "Target geometry was outside the picker");
            return;
        }

        Cancel(
            "Quoted full-name query still produced invalid target geometry; " +
                "currency treated as unavailable.",
            SinglePairScanFailureKind.CurrencyUnavailable);
    }

    private void StartQuotedQuery(
        CurrencySearchQueryController queryController,
        CurrencyCatalogue catalogue,
        string reason)
    {
        _quotedQueryRetryAttempted = true;
        var target = _activeWantedSide
            ? _step!.WantedCurrency
            : _step!.OfferedCurrency;
        if (!queryController.StartAutomatedQuotedFullName(
            target,
            _activeWantedSide,
            catalogue,
            out var retryFailure))
        {
            Cancel($"Quoted full-name query failed to start: {retryFailure}");
            return;
        }

        State = SinglePairScanState.EnteringQuery;
        Status = $"{reason}; retrying once with quoted full name " +
            $"{queryController.Query}.";
    }

    private void EnsurePair(
        GameController gameController,
        CurrencyCatalogue catalogue,
        PickerButtonCalibrationController calibration,
        CalibratedPickerOpenController pickerOpenController,
        CurrencySearchQueryController queryController,
        int cursorSpeed)
    {
        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (!panel.IsVisible)
        {
            Cancel("Single-pair scan cancelled: expected a visible exchange panel.");
            return;
        }

        if (panel.CurrencyPicker.IsVisible)
        {
            if (!_allowOpenPickerReuse)
            {
                Cancel("Single-pair scan cancelled: an unapproved picker is already open.");
                return;
            }

            _activeWantedSide = panel.CurrencyPicker.IsPickingWantedCurrency;
            var target = _activeWantedSide
                ? _step!.WantedCurrency
                : _step!.OfferedCurrency;
            var selectedMetadata = _activeWantedSide
                ? panel.WantedItemType?.Metadata
                : panel.OfferedItemType?.Metadata;
            if (selectedMetadata == target.Metadata)
            {
                Cancel("Open-picker continuation cannot advance because its side already matches the next target.");
                return;
            }

            _allowOpenPickerReuse = false;
            _quotedQueryRetryAttempted = false;
            if (!queryController.StartAutomated(
                target,
                _activeWantedSide,
                catalogue,
                out var queryFailure))
            {
                Cancel($"Open-picker continuation query failed to start: {queryFailure}");
                return;
            }

            State = SinglePairScanState.EnteringQuery;
            Status = $"Reusing open {SideName}; replacing search query for {target.Name}.";
            return;
        }

        _allowOpenPickerReuse = false;

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
            _rateReadFailed = true;
            Status = $"Waiting for stable rate: {captureFailure}";
            return;
        }

        if (snapshot!.Pair != _step!.Pair)
        {
            Cancel("Stable-rate capture failed: selected pair no longer matches the plan.");
            return;
        }

        var immediateRate = snapshot.TopImmediateStock?.SelectedPairRate;
        var competingRate = snapshot.TopCompetingStock?.RawRate;
        var observedRate = immediateRate ?? competingRate;
        if (observedRate == null)
        {
            _readableNoMarketSamples = _lastReadableNoMarketAtUtc != default &&
                now - _lastReadableNoMarketAtUtc <= MaximumNoMarketSampleGap
                    ? _readableNoMarketSamples + 1
                    : 1;
            _lastReadableNoMarketAtUtc = now;
            if (_readableNoMarketSamples >= 3 && !_sawPositiveMarketRate && !_rateReadFailed)
            {
                Cancel(
                    "No immediate or competing market rate was observed in three consecutive readable samples.",
                    SinglePairScanFailureKind.NoMarketRate);
                return;
            }

            Status = $"No immediate or competing market rate: confirming readable sample " +
                $"{_readableNoMarketSamples}/3.";
            return;
        }

        _sawPositiveMarketRate = true;

        if (observedRate.RawGet == _lastRateGet &&
            observedRate.RawGive == _lastRateGive)
        {
            _stableRateSamples++;
        }
        else
        {
            _lastRateGet = observedRate.RawGet;
            _lastRateGive = observedRate.RawGive;
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
