using ExileCore;
using System.Numerics;

namespace FaustusController;

public enum CursorTweenState
{
    Idle,
    Moving,
    Completed,
    Faulted
}

public sealed class CursorTweenController
{
    private const string TargetOutsidePickerFailure =
        "Cursor tween cancelled: target center is outside the picker rectangle.";
    private const float ManualInterruptionDistance = 25;
    private const float TargetMovementTolerance = 4;
    private static readonly TimeSpan MinimumDuration = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan MaximumDuration = TimeSpan.FromMilliseconds(650);

    private CurrencyIdentity? _targetCurrency;
    private bool _expectedWantedPicker;
    private Vector2 _start;
    private Vector2 _target;
    private Vector2 _control1;
    private Vector2 _control2;
    private Vector2 _lastCommandedPosition;
    private DateTimeOffset _startedAtUtc;
    private TimeSpan _duration;
    private float _curveDirection = 1;

    public CursorTweenState State { get; private set; } = CursorTweenState.Idle;
    public string Status { get; private set; } = "Verified target mouse movement is idle.";
    public bool IsRunning => State == CursorTweenState.Moving;
    public CurrencyIdentity? TargetCurrency => _targetCurrency;
    public bool ExpectedWantedPicker => _expectedWantedPicker;
    public Vector2 TargetPosition => _target;
    public bool FailedBecauseTargetIsOutsidePicker =>
        State == CursorTweenState.Faulted && Status == TargetOutsidePickerFailure;

    public bool Start(
        CurrencyIdentity targetCurrency,
        bool expectedWantedPicker,
        Vector2 target,
        int pixelsPerSecond,
        out string failureReason)
    {
        if (IsRunning)
        {
            failureReason = "A cursor tween is already running.";
            Status = failureReason;
            return false;
        }

        _start = Input.MousePositionNum;
        _targetCurrency = targetCurrency;
        _expectedWantedPicker = expectedWantedPicker;
        _target = target;
        var delta = target - _start;
        var distance = delta.Length();
        if (distance < 1)
        {
            State = CursorTweenState.Completed;
            Status = $"Cursor is already over verified {targetCurrency.Name}; no click sent.";
            failureReason = string.Empty;
            return true;
        }

        _lastCommandedPosition = _start;
        _duration = TimeSpan.FromSeconds(distance / Math.Max(pixelsPerSecond, 1));
        _duration = _duration < MinimumDuration
            ? MinimumDuration
            : _duration > MaximumDuration
                ? MaximumDuration
                : _duration;

        var direction = delta / distance;
        var perpendicular = new Vector2(-direction.Y, direction.X);
        var curveAmount = Math.Clamp(distance * 0.08f, 6, 40) * _curveDirection;
        _curveDirection *= -1;
        _control1 = _start + delta * 0.30f + perpendicular * curveAmount;
        _control2 = _start + delta * 0.72f + perpendicular * curveAmount * 0.55f;
        _startedAtUtc = DateTimeOffset.UtcNow;
        State = CursorTweenState.Moving;
        Status = $"Tweening cursor {distance:0}px to verified {targetCurrency.Name} over " +
            $"{_duration.TotalMilliseconds:0}ms; no click enabled.";
        failureReason = string.Empty;
        return true;
    }

    public void Tick(
        GameController gameController,
        CurrencyPickerInspector pickerInspector,
        CurrencyCatalogue catalogue)
    {
        if (!IsRunning)
        {
            return;
        }

        if (!TryResolveTarget(
            gameController,
            pickerInspector,
            catalogue,
            out var freshTarget,
            out var contextFailure))
        {
            Cancel(contextFailure);
            return;
        }

        if (Vector2.Distance(freshTarget!.Center, _target) > TargetMovementTolerance)
        {
            Cancel("Cursor tween cancelled: the verified target moved.");
            return;
        }

        var currentPosition = Input.MousePositionNum;
        if (Vector2.Distance(currentPosition, _lastCommandedPosition) > ManualInterruptionDistance)
        {
            Cancel("Cursor tween cancelled: manual mouse movement detected.");
            return;
        }

        var elapsed = DateTimeOffset.UtcNow - _startedAtUtc;
        var progress = Math.Clamp((float)(elapsed.TotalMilliseconds / _duration.TotalMilliseconds), 0, 1);
        var easedProgress = progress * progress * (3 - 2 * progress);
        var nextPosition = CubicBezier(
            _start,
            _control1,
            _control2,
            _target,
            easedProgress);

        if (!gameController.Window.IsForeground())
        {
            Cancel("Cursor tween cancelled: Path of Exile lost foreground.");
            return;
        }

        try
        {
            Input.SetCursorPos(nextPosition);
            _lastCommandedPosition = nextPosition;
        }
        catch (Exception exception)
        {
            Cancel($"Cursor tween failed: {exception.Message}");
            return;
        }

        if (progress >= 1)
        {
            State = CursorTweenState.Completed;
            Status = $"Cursor tween reached verified {_targetCurrency!.Name}; no click sent.";
        }
    }

    public void Cancel(string reason)
    {
        State = CursorTweenState.Faulted;
        Status = reason;
    }

    private bool TryResolveTarget(
        GameController gameController,
        CurrencyPickerInspector pickerInspector,
        CurrencyCatalogue catalogue,
        out CurrencyPickerOptionTarget? target,
        out string failureReason)
    {
        target = null;
        if (!gameController.Window.IsForeground())
        {
            failureReason = "Cursor tween cancelled: Path of Exile is not foreground.";
            return false;
        }

        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (!panel.IsVisible || !panel.CurrencyPicker.IsVisible)
        {
            failureReason = "Cursor tween cancelled: the Currency Exchange picker is not visible.";
            return false;
        }

        if (panel.CurrencyPicker.IsPickingWantedCurrency != _expectedWantedPicker)
        {
            failureReason = "Cursor tween cancelled: the picker side changed.";
            return false;
        }

        if (!pickerInspector.TryInspect(
            gameController,
            catalogue,
            out var inspection,
            out failureReason))
        {
            return false;
        }

        target = inspection!.VisibleOptions.FirstOrDefault(
            option => option.Currency.Metadata == _targetCurrency!.Metadata);
        if (target == null)
        {
            failureReason = "Cursor tween cancelled: the exact target is no longer visible.";
            return false;
        }

        var pickerRectangle = panel.CurrencyPicker.GetClientRectCache;
        if (target.Center.X < pickerRectangle.X ||
            target.Center.X > pickerRectangle.X + pickerRectangle.Width ||
            target.Center.Y < pickerRectangle.Y ||
            target.Center.Y > pickerRectangle.Y + pickerRectangle.Height)
        {
            target = null;
            failureReason = TargetOutsidePickerFailure;
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    private static Vector2 CubicBezier(
        Vector2 start,
        Vector2 control1,
        Vector2 control2,
        Vector2 target,
        float progress)
    {
        var inverse = 1 - progress;
        return inverse * inverse * inverse * start +
            3 * inverse * inverse * progress * control1 +
            3 * inverse * progress * progress * control2 +
            progress * progress * progress * target;
    }
}
