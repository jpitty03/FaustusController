using ExileCore;
using System.Numerics;

namespace FaustusController;

public enum CalibratedPickerOpenState
{
    Idle,
    MovingToButton,
    ReadyToClick,
    WaitingForPicker,
    Completed,
    Faulted
}

public sealed class CalibratedPickerOpenController
{
    private const float ManualInterruptionDistance = 25;
    private const float TargetMovementTolerance = 4;
    private const float ClickPositionTolerance = 6;
    private static readonly TimeSpan MinimumDuration = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan MaximumDuration = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(2);

    private bool _wantedButton;
    private Vector2 _start;
    private Vector2 _target;
    private Vector2 _control1;
    private Vector2 _control2;
    private Vector2 _lastCommandedPosition;
    private DateTimeOffset _startedAtUtc;
    private DateTimeOffset _openDeadlineUtc;
    private TimeSpan _duration;
    private float _curveDirection = 1;

    public CalibratedPickerOpenState State { get; private set; } =
        CalibratedPickerOpenState.Idle;
    public string Status { get; private set; } = "Calibrated picker opening is disabled by default.";
    public bool IsRunning => State is CalibratedPickerOpenState.MovingToButton or
        CalibratedPickerOpenState.ReadyToClick or
        CalibratedPickerOpenState.WaitingForPicker;

    public bool Start(
        GameController gameController,
        PickerButtonCalibrationController calibration,
        bool wantedButton,
        int pixelsPerSecond,
        out string failureReason)
    {
        if (IsRunning)
        {
            return FailStart("A picker-open operation is already running.", out failureReason);
        }

        if (!gameController.Window.IsForeground())
        {
            return FailStart("Picker open blocked: Path of Exile is not foreground.", out failureReason);
        }

        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (!panel.IsVisible || panel.CurrencyPicker.IsVisible)
        {
            return FailStart(
                "Picker open blocked: panel must be visible with its picker closed.",
                out failureReason);
        }

        if (!calibration.TryResolve(
            panel.GetClientRectCache,
            wantedButton,
            out var target))
        {
            return FailStart(
                $"Picker open blocked: {(wantedButton ? "I want" : "I have")} is not calibrated.",
                out failureReason);
        }

        _wantedButton = wantedButton;
        _start = Input.MousePositionNum;
        _target = target;
        _lastCommandedPosition = _start;
        var delta = _target - _start;
        var distance = delta.Length();
        if (distance < 1)
        {
            State = CalibratedPickerOpenState.ReadyToClick;
            Status = $"Cursor already at calibrated {SideName}; preparing one click.";
            failureReason = string.Empty;
            return true;
        }

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
        State = CalibratedPickerOpenState.MovingToButton;
        Status = $"Tweening to calibrated {SideName} over {_duration.TotalMilliseconds:0}ms.";
        failureReason = string.Empty;
        return true;
    }

    public void Tick(
        GameController gameController,
        PickerButtonCalibrationController calibration)
    {
        switch (State)
        {
            case CalibratedPickerOpenState.MovingToButton:
                TickMovement(gameController, calibration);
                return;
            case CalibratedPickerOpenState.ReadyToClick:
                ClickButton(gameController, calibration);
                return;
            case CalibratedPickerOpenState.WaitingForPicker:
                VerifyPickerOpened(gameController);
                return;
        }
    }

    public void Cancel(string reason)
    {
        State = CalibratedPickerOpenState.Faulted;
        Status = reason;
    }

    private string SideName => _wantedButton ? "I want" : "I have";

    private void TickMovement(
        GameController gameController,
        PickerButtonCalibrationController calibration)
    {
        if (!TryValidateClosedPicker(
            gameController,
            calibration,
            out var freshTarget,
            out var failureReason))
        {
            Cancel(failureReason);
            return;
        }

        if (Vector2.Distance(freshTarget, _target) > TargetMovementTolerance)
        {
            Cancel("Picker open cancelled: calibrated button moved during tween.");
            return;
        }

        if (Vector2.Distance(Input.MousePositionNum, _lastCommandedPosition) >
            ManualInterruptionDistance)
        {
            Cancel("Picker open cancelled: manual mouse movement detected.");
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

        try
        {
            Input.SetCursorPos(nextPosition);
            _lastCommandedPosition = nextPosition;
        }
        catch (Exception exception)
        {
            Cancel($"Picker-button tween failed: {exception.Message}");
            return;
        }

        if (progress >= 1)
        {
            State = CalibratedPickerOpenState.ReadyToClick;
            Status = $"Reached calibrated {SideName}; revalidating before one click.";
        }
    }

    private void ClickButton(
        GameController gameController,
        PickerButtonCalibrationController calibration)
    {
        if (!TryValidateClosedPicker(
            gameController,
            calibration,
            out var freshTarget,
            out var failureReason))
        {
            Cancel(failureReason);
            return;
        }

        if (Vector2.Distance(freshTarget, _target) > TargetMovementTolerance ||
            Vector2.Distance(Input.MousePositionNum, freshTarget) > ClickPositionTolerance)
        {
            Cancel("Picker open cancelled: cursor or calibrated button moved before click.");
            return;
        }

        var buttonDown = false;
        Exception? clickFailure = null;
        Exception? releaseFailure = null;
        try
        {
            buttonDown = true;
            Input.LeftDown();
        }
        catch (Exception exception)
        {
            clickFailure = exception;
        }
        finally
        {
            if (buttonDown)
            {
                try
                {
                    Input.LeftUp();
                }
                catch (Exception exception)
                {
                    releaseFailure = exception;
                }
            }
        }

        if (clickFailure != null)
        {
            Cancel($"Picker-button click failed: {clickFailure.Message}");
            return;
        }

        if (releaseFailure != null)
        {
            Cancel($"Picker-button release failed: {releaseFailure.Message}");
            return;
        }

        _openDeadlineUtc = DateTimeOffset.UtcNow + OpenTimeout;
        State = CalibratedPickerOpenState.WaitingForPicker;
        Status = $"Clicked calibrated {SideName}; waiting for picker verification.";
    }

    private void VerifyPickerOpened(GameController gameController)
    {
        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (!panel.IsVisible)
        {
            Cancel("Picker-open verification failed: Currency Exchange panel closed.");
            return;
        }

        if (panel.CurrencyPicker.IsVisible)
        {
            if (panel.CurrencyPicker.IsPickingWantedCurrency == _wantedButton)
            {
                State = CalibratedPickerOpenState.Completed;
                Status = $"Verified calibrated {SideName} picker opened after one click.";
            }
            else
            {
                Cancel("Picker-open verification failed: the opposite picker side opened.");
            }

            return;
        }

        if (DateTimeOffset.UtcNow >= _openDeadlineUtc)
        {
            Cancel($"Picker-open verification timed out for {SideName}; no retry sent.");
        }
    }

    private bool TryValidateClosedPicker(
        GameController gameController,
        PickerButtonCalibrationController calibration,
        out Vector2 target,
        out string failureReason)
    {
        target = default;
        if (!gameController.Window.IsForeground())
        {
            failureReason = "Picker open cancelled: Path of Exile is not foreground.";
            return false;
        }

        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (!panel.IsVisible || panel.CurrencyPicker.IsVisible)
        {
            failureReason = "Picker open cancelled: panel changed or a picker is already visible.";
            return false;
        }

        if (!calibration.TryResolve(
            panel.GetClientRectCache,
            _wantedButton,
            out target))
        {
            failureReason = $"Picker open cancelled: calibrated {SideName} is unavailable.";
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    private bool FailStart(string reason, out string failureReason)
    {
        Cancel(reason);
        failureReason = reason;
        return false;
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
