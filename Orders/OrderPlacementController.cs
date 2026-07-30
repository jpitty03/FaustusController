using ExileCore;
using System.Numerics;
using System.Windows.Forms;

namespace FaustusController;

public enum OrderPlacementState
{
    Idle,
    AwaitingStagedOrder,
    MovingToButton,
    ReadyToClick,
    WaitingForOrder,
    Completed,
    Faulted
}

/// <summary>
/// Clicks the calibrated Place Order button after <see cref="OrderStagingController"/>
/// reaches <see cref="OrderStagingState.Staged"/>, then verifies through live memory
/// that a new placed order for the staged pair actually appeared. Every phase
/// revalidates the panel, the pair, the typed digits, and the calibrated target;
/// any mismatch cancels with a reason instead of retrying.
/// </summary>
public sealed class OrderPlacementController
{
    private static readonly TimeSpan MinimumMoveDuration = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan MaximumMoveDuration = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan PlacementTimeout = TimeSpan.FromSeconds(3);
    private const float TargetMovementTolerance = 4f;
    private const float ClickPositionTolerance = 6f;
    private const float ManualInterruptionDistance = 25f;

    private CurrencyScanPlanStep? _step;
    private long _offeredAmount;
    private long _wantedAmount;
    private Vector2 _movementStart;
    private Vector2 _control1;
    private Vector2 _control2;
    private Vector2 _target;
    private Vector2 _lastCommandedPosition;
    private DateTimeOffset _movementStartUtc;
    private TimeSpan _movementDuration;
    private DateTimeOffset _placementDeadlineUtc;
    private HashSet<int> _baselineOrderIds = [];
    private bool _completedNotificationPending;

    public OrderPlacementState State { get; private set; } = OrderPlacementState.Idle;
    public string Status { get; private set; } =
        "Order placement is disabled by default.";
    public bool IsRunning => State is OrderPlacementState.AwaitingStagedOrder or
        OrderPlacementState.MovingToButton or
        OrderPlacementState.ReadyToClick or
        OrderPlacementState.WaitingForOrder;

    public bool Start(
        GameController gameController,
        PickerButtonCalibrationController calibration,
        out string failureReason)
    {
        if (IsRunning)
        {
            failureReason = "Order placement is already running.";
            return false;
        }

        if (!gameController.Window.IsForeground())
        {
            return FailStart(
                "Order placement blocked: Path of Exile is not foreground.",
                out failureReason);
        }

        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (!panel.IsVisible || panel.CurrencyPicker.IsVisible)
        {
            return FailStart(
                "Order placement blocked: the exchange panel must be visible with no open picker.",
                out failureReason);
        }

        if (!calibration.TryResolvePlaceOrder(panel.GetClientRectCache, out _))
        {
            return FailStart(
                "Order placement blocked: hover Place Order and press F9 to calibrate it first.",
                out failureReason);
        }

        if (panel.Orders == null)
        {
            return FailStart(
                "Order placement blocked: placed orders are unavailable on the panel.",
                out failureReason);
        }

        _step = null;
        _offeredAmount = 0;
        _wantedAmount = 0;
        _completedNotificationPending = false;
        State = OrderPlacementState.AwaitingStagedOrder;
        Status = "Order placement armed: waiting for staging to verify the order.";
        failureReason = string.Empty;
        return true;
    }

    public void Tick(
        GameController gameController,
        OrderStagingController stagingController,
        PickerButtonCalibrationController calibration,
        int cursorSpeed)
    {
        if (!IsRunning)
        {
            return;
        }

        if (!gameController.Window.IsForeground())
        {
            Cancel("Order placement cancelled: Path of Exile lost foreground.");
            return;
        }

        switch (State)
        {
            case OrderPlacementState.AwaitingStagedOrder:
                TickAwaitingStagedOrder(gameController, stagingController, calibration, cursorSpeed);
                break;
            case OrderPlacementState.MovingToButton:
                TickMovingToButton(gameController, calibration);
                break;
            case OrderPlacementState.ReadyToClick:
                ClickPlaceOrder(gameController, calibration);
                break;
            case OrderPlacementState.WaitingForOrder:
                VerifyOrderPlaced(gameController);
                break;
        }
    }

    /// <summary>
    /// Returns true exactly once after a placement completes so the host can
    /// persist the refreshed placed-orders export.
    /// </summary>
    public bool TakeCompletedNotification()
    {
        if (!_completedNotificationPending)
        {
            return false;
        }

        _completedNotificationPending = false;
        return true;
    }

    public void Cancel(string reason)
    {
        State = OrderPlacementState.Faulted;
        Status = reason;
    }

    private void TickAwaitingStagedOrder(
        GameController gameController,
        OrderStagingController stagingController,
        PickerButtonCalibrationController calibration,
        int cursorSpeed)
    {
        if (stagingController.State == OrderStagingState.Faulted)
        {
            Cancel($"Order placement cancelled: staging failed. {stagingController.Status}");
            return;
        }

        if (stagingController.State != OrderStagingState.Staged)
        {
            Status = $"Order placement waiting on staging: {stagingController.Status}";
            return;
        }

        var step = stagingController.CurrentStep;
        if (step == null)
        {
            Cancel("Order placement cancelled: the staged step is unavailable.");
            return;
        }

        _step = step;
        _offeredAmount = stagingController.OfferedAmount;
        _wantedAmount = stagingController.WantedAmount;
        if (!TryValidatePlacementContext(
            gameController,
            calibration,
            out var target,
            out var failureReason))
        {
            Cancel(failureReason);
            return;
        }

        BeginMovement(target, cursorSpeed);
    }

    private void BeginMovement(Vector2 target, int cursorSpeed)
    {
        _target = target;
        _movementStart = Input.MousePositionNum;
        _lastCommandedPosition = _movementStart;
        var distance = Vector2.Distance(_movementStart, target);
        if (distance < 1f)
        {
            State = OrderPlacementState.ReadyToClick;
            Status = "Cursor already at Place Order; clicking after revalidation.";
            return;
        }

        var direction = Vector2.Normalize(target - _movementStart);
        var perpendicular = new Vector2(-direction.Y, direction.X);
        var curve = Math.Min(distance * 0.18f, 40f);
        _control1 = _movementStart + direction * (distance * 0.3f) + perpendicular * curve;
        _control2 = _movementStart + direction * (distance * 0.7f) - perpendicular * (curve * 0.5f);
        _movementDuration = TimeSpan.FromSeconds(Math.Clamp(
            distance / Math.Max(cursorSpeed, 1),
            MinimumMoveDuration.TotalSeconds,
            MaximumMoveDuration.TotalSeconds));
        _movementStartUtc = DateTimeOffset.UtcNow;
        State = OrderPlacementState.MovingToButton;
        Status = "Moving to the calibrated Place Order button.";
    }

    private void TickMovingToButton(
        GameController gameController,
        PickerButtonCalibrationController calibration)
    {
        if (!TryValidatePlacementContext(
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
            Cancel("Order placement cancelled: the Place Order button moved during the tween.");
            return;
        }

        if (Vector2.Distance(Input.MousePositionNum, _lastCommandedPosition) >
            ManualInterruptionDistance)
        {
            Cancel("Order placement cancelled: manual mouse movement detected.");
            return;
        }

        var elapsed = DateTimeOffset.UtcNow - _movementStartUtc;
        var progress = Math.Clamp(
            (float)(elapsed.TotalMilliseconds / _movementDuration.TotalMilliseconds),
            0f,
            1f);
        var next = CubicBezier(_movementStart, _control1, _control2, _target, progress);
        Input.SetCursorPos(next);
        _lastCommandedPosition = next;
        if (progress >= 1f)
        {
            State = OrderPlacementState.ReadyToClick;
            Status = "Arrived at Place Order; clicking after revalidation.";
        }
    }

    private void ClickPlaceOrder(
        GameController gameController,
        PickerButtonCalibrationController calibration)
    {
        if (!TryValidatePlacementContext(
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
            Cancel("Order placement cancelled: the Place Order button moved before the click.");
            return;
        }

        if (Vector2.Distance(Input.MousePositionNum, freshTarget) > ClickPositionTolerance)
        {
            Cancel("Order placement cancelled: the cursor is no longer on Place Order.");
            return;
        }

        if (Input.IsKeyDown(Keys.ControlKey) || Input.IsKeyDown(Keys.ShiftKey) ||
            Input.IsKeyDown(Keys.Menu))
        {
            Cancel("Order placement cancelled: release Ctrl, Shift, and Alt.");
            return;
        }

        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        var placedOrders = panel.Orders;
        if (placedOrders == null)
        {
            Cancel("Order placement cancelled: placed orders became unavailable before the click.");
            return;
        }

        var baseline = new HashSet<int>();
        foreach (var order in placedOrders)
        {
            if (order != null)
            {
                baseline.Add(order.PlayerOrderId);
            }
        }

        _baselineOrderIds = baseline;

        Exception? clickFailure = null;
        Exception? releaseFailure = null;
        var buttonDown = false;
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
            Cancel($"Place Order click failed: {clickFailure.Message}");
            return;
        }

        if (releaseFailure != null)
        {
            Cancel($"Place Order release failed: {releaseFailure.Message}");
            return;
        }

        _placementDeadlineUtc = DateTimeOffset.UtcNow + PlacementTimeout;
        State = OrderPlacementState.WaitingForOrder;
        Status = "Clicked Place Order; waiting for the new order to appear.";
    }

    private void VerifyOrderPlaced(GameController gameController)
    {
        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (!panel.IsVisible)
        {
            Cancel("Order placement verification failed: the exchange panel closed; " +
                "check the placed-orders tab manually before retrying.");
            return;
        }

        var placedOrders = panel.Orders;
        if (placedOrders != null)
        {
            foreach (var order in placedOrders)
            {
                if (order == null || _baselineOrderIds.Contains(order.PlayerOrderId))
                {
                    continue;
                }

                if (order.OfferedItemType?.Metadata != _step!.OfferedCurrency.Metadata ||
                    order.WantedItemType?.Metadata != _step.WantedCurrency.Metadata)
                {
                    continue;
                }

                var orderStatus = order.IsCanceled
                    ? "Canceled"
                    : order.IsCompleted
                        ? "Completed (filled)"
                        : "Pending";
                State = OrderPlacementState.Completed;
                _completedNotificationPending = true;
                Status = $"Order placed: {_offeredAmount} {_step.OfferedCurrency.Name} -> " +
                    $"{_wantedAmount} {_step.WantedCurrency.Name}; exchange reports " +
                    $"order {order.PlayerOrderId} as {orderStatus}.";
                return;
            }
        }

        if (DateTimeOffset.UtcNow >= _placementDeadlineUtc)
        {
            Cancel("Order placement verification timed out: no new order for the staged " +
                "pair appeared within 3s; check gold and the placed-orders tab before retrying.");
        }
    }

    private bool TryValidatePlacementContext(
        GameController gameController,
        PickerButtonCalibrationController calibration,
        out Vector2 target,
        out string failureReason)
    {
        target = default;
        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (!panel.IsVisible)
        {
            failureReason = "Order placement cancelled: the exchange panel is no longer visible.";
            return false;
        }

        if (panel.CurrencyPicker.IsVisible)
        {
            failureReason = "Order placement cancelled: a currency picker is unexpectedly open.";
            return false;
        }

        if (panel.OfferedItemType?.Metadata != _step!.OfferedCurrency.Metadata ||
            panel.WantedItemType?.Metadata != _step.WantedCurrency.Metadata)
        {
            failureReason = "Order placement cancelled: the panel's selected currencies " +
                "no longer match the staged pair.";
            return false;
        }

        var offeredDigits = CurrencyAmountInputController.ReadDigits(panel.OfferedItemCountInput);
        var wantedDigits = CurrencyAmountInputController.ReadDigits(panel.WantedItemCountInput);
        if (offeredDigits != _offeredAmount.ToString() ||
            wantedDigits != _wantedAmount.ToString())
        {
            failureReason = "Order placement cancelled: the staged amounts changed " +
                $"(have '{offeredDigits}', want '{wantedDigits}').";
            return false;
        }

        if (!calibration.TryResolvePlaceOrder(panel.GetClientRectCache, out target))
        {
            failureReason =
                "Order placement cancelled: the calibrated Place Order button is unavailable.";
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
