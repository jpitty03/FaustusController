namespace FaustusController;

public sealed partial class FaustusController
{
    private string _orderCollectionStatus =
        "Press F11 to collect completed orders into stash " +
        "(enable Allow Order Collection; open exchange + stash + inventory).";

    private void StartOrderCollection()
    {
        // Pressing F11 while a run is in flight aborts it.
        if (_orderCollectionController.IsRunning)
        {
            CancelOrderCollection("Order collection cancelled by hotkey.");
            return;
        }

        if (!Settings.AllowOrderCollection)
        {
            _orderCollectionStatus =
                "Order collection blocked: enable Allow Order Collection first.";
            return;
        }

        if (IsAnyAutomationRunning)
        {
            _orderCollectionStatus = "Order collection blocked: another automation is running.";
            return;
        }

        if (_pickerOpenController.IsRunning || _searchQueryController.IsRunning ||
            _cursorTweenController.IsRunning || _selectionController.IsRunning ||
            _amountInputController.IsRunning)
        {
            _orderCollectionStatus =
                "Order collection blocked: another input operation is running.";
            return;
        }

        if (!_orderCollectionController.Start(
            GameController,
            Settings.CursorTweenSpeed.Value,
            out var failureReason))
        {
            _orderCollectionStatus = failureReason;
            return;
        }

        _orderCollectionStatus = _orderCollectionController.Status;
    }

    private void TickOrderCollection()
    {
        if (_orderCollectionController.IsRunning && !Settings.AllowOrderCollection)
        {
            CancelOrderCollection(
                "Order collection cancelled: Allow Order Collection was disabled.");
            return;
        }

        _orderCollectionController.Tick(GameController, Settings.CursorTweenSpeed.Value);

        // Leave the initial prompt untouched before a run; otherwise mirror the
        // controller's live/terminal status.
        if (_orderCollectionController.State != OrderCollectionState.Idle)
        {
            _orderCollectionStatus = _orderCollectionController.Status;
        }
    }

    private void CancelOrderCollection(string reason)
    {
        _orderCollectionController.Cancel(reason);
        _orderCollectionStatus = reason;
    }
}
