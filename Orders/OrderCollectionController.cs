using ExileCore;
using ExileCore.Shared.Enums;
using System.Numerics;
using System.Windows.Forms;

namespace FaustusController;

public enum OrderCollectionState
{
    Idle,
    Moving,
    Clicking,
    Verifying,
    Completed,
    Faulted
}

/// <summary>
/// Verified two-phase order collection (F11). Phase 1 ctrl-right-clicks each Completed
/// exchange order's bought-currency slot (child &lt;4&gt;) — and its leftover offered slot
/// (child &lt;5&gt;) when offered units remain — to move that currency into the inventory.
/// Phase 2 ctrl+shift+right-clicks one visible inventory stack of each collected currency,
/// which sweeps every stack of that type (visible and covered) into the stash. Requires the
/// exchange, stash, and inventory all open. It only clicks inventory stacks whose whole rect
/// clears the exchange window's right edge, so the covered left columns must be blocked by the
/// player; anything that lands covered is reported, never blindly clicked. Every phase tweens
/// to the target, revalidates against live memory, and cancels with a reason on any mismatch.
/// </summary>
public sealed class OrderCollectionController
{
    private static readonly TimeSpan MinimumMoveDuration = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan MaximumMoveDuration = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(3);
    private const float TargetDriftTolerance = 6f;
    private const float ClickPositionTolerance = 8f;
    private const float ManualInterruptionDistance = 25f;
    private const int MaximumClicks = 60;
    private const int BoughtSlotIndex = 4;
    private const int LeftoverSlotIndex = 5;

    // Collected currencies in first-seen order, plus per-metadata display name and
    // the stashed / unreachable outcomes recorded during phase 2.
    private readonly List<string> _collectedOrder = [];
    private readonly Dictionary<string, string> _collectedNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _stashed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unreachable = new(StringComparer.Ordinal);

    private enum Phase
    {
        Orders,
        Inventory
    }

    private Phase _phase;
    private bool _shiftClick;
    private int _orderId;
    private int _slotIndex;
    private string _targetPath = "";
    private string _targetName = "";
    private Vector2 _targetCenter;

    private Vector2 _movementStart;
    private Vector2 _control1;
    private Vector2 _control2;
    private Vector2 _lastCommandedPosition;
    private DateTimeOffset _movementStartUtc;
    private TimeSpan _movementDuration;
    private DateTimeOffset _verifyDeadlineUtc;

    private int _clicks;
    private int _orderSlotsCollected;

    public OrderCollectionState State { get; private set; } = OrderCollectionState.Idle;
    public string Status { get; private set; } = "Order collection is disabled by default.";
    public bool IsRunning => State is OrderCollectionState.Moving or
        OrderCollectionState.Clicking or OrderCollectionState.Verifying;

    public bool Start(GameController gameController, int cursorSpeed, out string failureReason)
    {
        if (IsRunning)
        {
            return Fail("Order collection is already running.", out failureReason);
        }

        if (!TryValidateContext(gameController, out var contextFailure))
        {
            return Fail($"Order collection blocked: {contextFailure}", out failureReason);
        }

        _collectedOrder.Clear();
        _collectedNames.Clear();
        _stashed.Clear();
        _unreachable.Clear();
        _clicks = 0;
        _orderSlotsCollected = 0;
        _phase = Phase.Orders;
        State = OrderCollectionState.Moving;
        Status = "Order collection: scanning completed orders.";
        failureReason = string.Empty;

        AdvanceToNextTarget(gameController, cursorSpeed);
        return State != OrderCollectionState.Faulted;
    }

    public void Tick(GameController gameController, int cursorSpeed)
    {
        if (!IsRunning)
        {
            return;
        }

        if (!TryValidateContext(gameController, out var failure))
        {
            Cancel($"Order collection cancelled: {failure}");
            return;
        }

        switch (State)
        {
            case OrderCollectionState.Moving:
                TickMoving(gameController, cursorSpeed);
                return;
            case OrderCollectionState.Clicking:
                TickClicking(gameController, cursorSpeed);
                return;
            case OrderCollectionState.Verifying:
                TickVerifying(gameController, cursorSpeed);
                return;
        }
    }

    public void Cancel(string reason)
    {
        State = OrderCollectionState.Faulted;
        Status = reason;
    }

    // ---- Target selection -------------------------------------------------

    private void AdvanceToNextTarget(GameController gameController, int cursorSpeed)
    {
        if (_clicks >= MaximumClicks)
        {
            Cancel($"Order collection stopped after {_clicks} clicks (safety cap); " +
                "check the exchange and inventory manually.");
            return;
        }

        if (_phase == Phase.Orders)
        {
            if (TryFindOrderTarget(gameController))
            {
                BeginMovement(cursorSpeed);
                return;
            }

            _phase = Phase.Inventory;
        }

        if (TryFindInventoryTarget(gameController))
        {
            BeginMovement(cursorSpeed);
            return;
        }

        Complete();
    }

    private bool TryFindOrderTarget(GameController gameController)
    {
        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        var orders = panel.Orders;
        var elements = panel.OrderElements;
        if (orders == null || elements == null)
        {
            return false;
        }

        for (var i = 0; i < orders.Count && i < elements.Count; i++)
        {
            var order = orders[i];
            var element = elements[i];
            if (order == null || element == null || !order.IsCompleted)
            {
                continue;
            }

            if (TrySlotCenter(element, BoughtSlotIndex, out var boughtCenter) &&
                order.WantedItemType?.Metadata is { Length: > 0 } boughtMetadata)
            {
                SetOrderTarget(
                    order.PlayerOrderId,
                    BoughtSlotIndex,
                    boughtMetadata,
                    order.WantedItemType?.BaseName ?? boughtMetadata,
                    boughtCenter);
                return true;
            }

            if (order.OfferedItemStackSize > 0 &&
                TrySlotCenter(element, LeftoverSlotIndex, out var leftoverCenter) &&
                order.OfferedItemType?.Metadata is { Length: > 0 } offeredMetadata)
            {
                SetOrderTarget(
                    order.PlayerOrderId,
                    LeftoverSlotIndex,
                    offeredMetadata,
                    order.OfferedItemType?.BaseName ?? offeredMetadata,
                    leftoverCenter);
                return true;
            }
        }

        return false;
    }

    private void SetOrderTarget(
        int orderId,
        int slotIndex,
        string metadata,
        string name,
        Vector2 center)
    {
        _shiftClick = false;
        _orderId = orderId;
        _slotIndex = slotIndex;
        _targetPath = metadata;
        _targetName = name;
        _targetCenter = center;
        if (!_collectedNames.ContainsKey(metadata))
        {
            _collectedOrder.Add(metadata);
            _collectedNames[metadata] = name;
        }
    }

    private bool TryFindInventoryTarget(GameController gameController)
    {
        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        var exchangeRight = panel.GetClientRectCache.Right;
        var inventory = gameController.Game.IngameState.IngameUi
            .InventoryPanel?[InventoryIndex.PlayerInventory];
        var items = inventory?.VisibleInventoryItems;

        foreach (var metadata in _collectedOrder)
        {
            if (_stashed.Contains(metadata) || _unreachable.Contains(metadata))
            {
                continue;
            }

            var present = 0;
            Vector2? visibleCenter = null;
            if (items != null)
            {
                foreach (var item in items)
                {
                    if (item?.Item?.Path != metadata)
                    {
                        continue;
                    }

                    present++;
                    var rectangle = item.GetClientRectCache;
                    if (visibleCenter == null && rectangle.Left >= exchangeRight)
                    {
                        visibleCenter = new Vector2(
                            rectangle.X + rectangle.Width / 2f,
                            rectangle.Y + rectangle.Height / 2f);
                    }
                }
            }

            if (present == 0)
            {
                // Already gone (e.g. an earlier sweep of the same type moved it).
                _stashed.Add(metadata);
                continue;
            }

            if (visibleCenter == null)
            {
                // Only covered stacks exist; the player must unblock/rearrange.
                _unreachable.Add(metadata);
                continue;
            }

            _shiftClick = true;
            _targetPath = metadata;
            _targetName = _collectedNames.GetValueOrDefault(metadata, metadata);
            _targetCenter = visibleCenter.Value;
            return true;
        }

        return false;
    }

    // ---- Movement ---------------------------------------------------------

    private void BeginMovement(int cursorSpeed)
    {
        _movementStart = Input.MousePositionNum;
        _lastCommandedPosition = _movementStart;
        var distance = Vector2.Distance(_movementStart, _targetCenter);
        if (distance < 1f)
        {
            State = OrderCollectionState.Clicking;
            Status = DescribeTarget("cursor already on target; clicking after revalidation");
            return;
        }

        var direction = Vector2.Normalize(_targetCenter - _movementStart);
        var perpendicular = new Vector2(-direction.Y, direction.X);
        var curve = Math.Min(distance * 0.18f, 40f);
        _control1 = _movementStart + direction * (distance * 0.3f) + perpendicular * curve;
        _control2 = _movementStart + direction * (distance * 0.7f) - perpendicular * (curve * 0.5f);
        _movementDuration = TimeSpan.FromSeconds(Math.Clamp(
            distance / Math.Max(cursorSpeed, 1),
            MinimumMoveDuration.TotalSeconds,
            MaximumMoveDuration.TotalSeconds));
        _movementStartUtc = DateTimeOffset.UtcNow;
        State = OrderCollectionState.Moving;
        Status = DescribeTarget("moving to target");
    }

    private void TickMoving(GameController gameController, int cursorSpeed)
    {
        if (!TryResolveCurrentTargetCenter(gameController, out var freshCenter))
        {
            Cancel($"Order collection cancelled: {DescribeTarget("target disappeared mid-move")}.");
            return;
        }

        if (Vector2.Distance(freshCenter, _targetCenter) > TargetDriftTolerance)
        {
            Cancel($"Order collection cancelled: {DescribeTarget("target moved during the tween")}.");
            return;
        }

        if (Vector2.Distance(Input.MousePositionNum, _lastCommandedPosition) >
            ManualInterruptionDistance)
        {
            Cancel("Order collection cancelled: manual mouse movement detected.");
            return;
        }

        var elapsed = DateTimeOffset.UtcNow - _movementStartUtc;
        var progress = Math.Clamp(
            (float)(elapsed.TotalMilliseconds / _movementDuration.TotalMilliseconds),
            0f,
            1f);
        var next = CubicBezier(_movementStart, _control1, _control2, _targetCenter, progress);
        Input.SetCursorPos(next);
        _lastCommandedPosition = next;
        if (progress >= 1f)
        {
            State = OrderCollectionState.Clicking;
            Status = DescribeTarget("arrived; clicking after revalidation");
        }
    }

    private void TickClicking(GameController gameController, int cursorSpeed)
    {
        if (!TryResolveCurrentTargetCenter(gameController, out var freshCenter))
        {
            Cancel($"Order collection cancelled: {DescribeTarget("target disappeared before the click")}.");
            return;
        }

        if (Vector2.Distance(freshCenter, _targetCenter) > TargetDriftTolerance)
        {
            Cancel($"Order collection cancelled: {DescribeTarget("target moved before the click")}.");
            return;
        }

        if (Vector2.Distance(Input.MousePositionNum, freshCenter) > ClickPositionTolerance)
        {
            Cancel("Order collection cancelled: the cursor is no longer on the target.");
            return;
        }

        if (Input.IsKeyDown(Keys.Menu))
        {
            Cancel("Order collection cancelled: release Alt.");
            return;
        }

        if (!TryModifiedRightClick(_shiftClick, out var clickFailure))
        {
            Cancel($"Order collection click failed: {clickFailure}");
            return;
        }

        _clicks++;
        _verifyDeadlineUtc = DateTimeOffset.UtcNow + VerifyTimeout;
        State = OrderCollectionState.Verifying;
        Status = DescribeTarget("clicked; verifying the move");
    }

    private void TickVerifying(GameController gameController, int cursorSpeed)
    {
        var done = _phase == Phase.Orders
            ? !IsOrderSlotCollectible(gameController, _orderId, _slotIndex)
            : InventoryCount(gameController, _targetPath) == 0;
        if (done)
        {
            if (_phase == Phase.Orders)
            {
                _orderSlotsCollected++;
            }
            else
            {
                _stashed.Add(_targetPath);
            }

            AdvanceToNextTarget(gameController, cursorSpeed);
            return;
        }

        if (DateTimeOffset.UtcNow >= _verifyDeadlineUtc)
        {
            var reason = _phase == Phase.Orders
                ? $"the completed order slot for {_targetName} did not clear (inventory full?)"
                : $"{_targetName} did not move to the stash (stash full?)";
            Cancel($"Order collection stopped: {reason}; " +
                $"collected {_orderSlotsCollected} slot(s), stashed {_stashed.Count} currency(ies).");
        }
    }

    private void Complete()
    {
        State = OrderCollectionState.Completed;
        var summary = $"Order collection done: collected {_orderSlotsCollected} order slot(s), " +
            $"stashed {_stashed.Count} currency(ies)";
        if (_unreachable.Count > 0)
        {
            var names = _unreachable
                .Select(metadata => _collectedNames.GetValueOrDefault(metadata, metadata));
            summary += $"; {_unreachable.Count} covered/unreachable in inventory " +
                $"({string.Join(", ", names)}) — unblock the left columns and press F11 again";
        }

        Status = summary + ".";
    }

    // ---- Live re-reads ----------------------------------------------------

    private bool TryResolveCurrentTargetCenter(GameController gameController, out Vector2 center)
    {
        center = default;
        if (_phase == Phase.Orders)
        {
            var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
            var orders = panel.Orders;
            var elements = panel.OrderElements;
            if (orders == null || elements == null)
            {
                return false;
            }

            for (var i = 0; i < orders.Count && i < elements.Count; i++)
            {
                if (orders[i]?.PlayerOrderId != _orderId)
                {
                    continue;
                }

                return TrySlotCenter(elements[i], _slotIndex, out center);
            }

            return false;
        }

        var exchangeRight = gameController.Game.IngameState.IngameUi
            .CurrencyExchangePanel.GetClientRectCache.Right;
        var inventory = gameController.Game.IngameState.IngameUi
            .InventoryPanel?[InventoryIndex.PlayerInventory];
        var items = inventory?.VisibleInventoryItems;
        if (items == null)
        {
            return false;
        }

        foreach (var item in items)
        {
            if (item?.Item?.Path != _targetPath)
            {
                continue;
            }

            var rectangle = item.GetClientRectCache;
            if (rectangle.Left < exchangeRight)
            {
                continue;
            }

            center = new Vector2(
                rectangle.X + rectangle.Width / 2f,
                rectangle.Y + rectangle.Height / 2f);
            return true;
        }

        return false;
    }

    private static bool IsOrderSlotCollectible(
        GameController gameController,
        int orderId,
        int slotIndex)
    {
        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        var orders = panel.Orders;
        var elements = panel.OrderElements;
        if (orders == null || elements == null)
        {
            return false;
        }

        for (var i = 0; i < orders.Count && i < elements.Count; i++)
        {
            if (orders[i]?.PlayerOrderId != orderId)
            {
                continue;
            }

            return TrySlotCenter(elements[i], slotIndex, out _);
        }

        return false;
    }

    private static int InventoryCount(GameController gameController, string path)
    {
        var inventory = gameController.Game.IngameState.IngameUi
            .InventoryPanel?[InventoryIndex.PlayerInventory];
        var items = inventory?.VisibleInventoryItems;
        if (items == null)
        {
            return 0;
        }

        var count = 0;
        foreach (var item in items)
        {
            if (item?.Item?.Path == path)
            {
                count++;
            }
        }

        return count;
    }

    // A slot is collectible when its icon child (index 0) is visible, i.e. currency
    // is actually present in that slot of the completed order.
    private static bool TrySlotCenter(
        ExileCore.PoEMemory.Element orderElement,
        int slotIndex,
        out Vector2 center)
    {
        center = default;
        var children = orderElement?.Children;
        if (children == null || slotIndex >= children.Count)
        {
            return false;
        }

        var slot = children[slotIndex];
        var icon = slot?.Children;
        if (slot == null || icon == null || icon.Count == 0 || icon[0]?.IsVisible != true)
        {
            return false;
        }

        var rectangle = slot.GetClientRectCache;
        if (rectangle.Width <= 0 || rectangle.Height <= 0)
        {
            return false;
        }

        center = new Vector2(
            rectangle.X + rectangle.Width / 2f,
            rectangle.Y + rectangle.Height / 2f);
        return true;
    }

    private bool TryValidateContext(GameController gameController, out string failureReason)
    {
        if (!gameController.Window.IsForeground())
        {
            failureReason = "Path of Exile is not foreground.";
            return false;
        }

        var ingameUi = gameController.Game.IngameState.IngameUi;
        var panel = ingameUi.CurrencyExchangePanel;
        if (!panel.IsVisible)
        {
            failureReason = "the currency exchange panel is not visible.";
            return false;
        }

        if (panel.CurrencyPicker.IsVisible)
        {
            failureReason = "a currency picker is unexpectedly open.";
            return false;
        }

        if (ingameUi.StashElement?.IsVisible != true)
        {
            failureReason = "the stash must be open.";
            return false;
        }

        if (ingameUi.InventoryPanel?.IsVisible != true)
        {
            failureReason = "the inventory must be open.";
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    private static bool TryModifiedRightClick(bool shift, out string failureReason)
    {
        failureReason = string.Empty;
        var controlDown = false;
        var shiftDown = false;
        try
        {
            Input.KeyDown(Keys.ControlKey);
            controlDown = true;
            if (shift)
            {
                Input.KeyDown(Keys.ShiftKey);
                shiftDown = true;
            }

            Input.RightDown();
            Input.RightUp();
        }
        catch (Exception exception)
        {
            failureReason = exception.Message;
            return false;
        }
        finally
        {
            if (shiftDown)
            {
                try
                {
                    Input.KeyUp(Keys.ShiftKey);
                }
                catch
                {
                    // Best effort: never leave Shift stuck down.
                }
            }

            if (controlDown)
            {
                try
                {
                    Input.KeyUp(Keys.ControlKey);
                }
                catch
                {
                    // Best effort: never leave Ctrl stuck down.
                }
            }
        }

        return failureReason.Length == 0;
    }

    private string DescribeTarget(string action)
    {
        return _phase == Phase.Orders
            ? $"Order collection: {action} — {_targetName} to inventory (ctrl-right-click)"
            : $"Order collection: {action} — {_targetName} to stash (ctrl+shift+right-click)";
    }

    private bool Fail(string reason, out string failureReason)
    {
        State = OrderCollectionState.Faulted;
        Status = reason;
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
