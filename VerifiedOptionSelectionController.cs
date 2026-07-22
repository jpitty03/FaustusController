using ExileCore;
using System.Numerics;

namespace FaustusController;

public enum VerifiedOptionSelectionState
{
    Idle,
    WaitingForSelection,
    Completed,
    Faulted
}

public sealed class VerifiedOptionSelectionController
{
    private static readonly TimeSpan SelectionTimeout = TimeSpan.FromSeconds(2);

    private CurrencyIdentity? _targetCurrency;
    private bool _expectedWantedPicker;
    private DateTimeOffset _selectionDeadlineUtc;

    public VerifiedOptionSelectionState State { get; private set; } =
        VerifiedOptionSelectionState.Idle;
    public string Status { get; private set; } = "Verified option clicking is disabled by default.";
    public bool IsRunning => State == VerifiedOptionSelectionState.WaitingForSelection;

    public bool TryClick(
        GameController gameController,
        CurrencyPickerInspector pickerInspector,
        CurrencyCatalogue catalogue,
        CurrencySearchQueryController queryController,
        CursorTweenController tweenController,
        out string failureReason)
    {
        if (IsRunning)
        {
            failureReason = "An option selection is already being verified.";
            Status = failureReason;
            return false;
        }

        if (queryController.State != CurrencySearchQueryState.Completed ||
            queryController.TargetCurrency == null ||
            tweenController.State != CursorTweenState.Completed ||
            tweenController.TargetCurrency == null)
        {
            return FailStart(
                "Click blocked: complete F9 verification and F10 tween first.",
                out failureReason);
        }

        if (queryController.TargetCurrency.Metadata != tweenController.TargetCurrency.Metadata ||
            queryController.ExpectedWantedPicker != tweenController.ExpectedWantedPicker)
        {
            return FailStart(
                "Click blocked: query and tween targets do not match.",
                out failureReason);
        }

        if (!gameController.Window.IsForeground())
        {
            return FailStart(
                "Click blocked: Path of Exile is not foreground.",
                out failureReason);
        }

        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (!panel.IsVisible || !panel.CurrencyPicker.IsVisible)
        {
            return FailStart(
                "Click blocked: the Currency Exchange picker is not visible.",
                out failureReason);
        }

        if (panel.CurrencyPicker.IsPickingWantedCurrency !=
            queryController.ExpectedWantedPicker)
        {
            return FailStart(
                "Click blocked: the picker side changed after verification.",
                out failureReason);
        }

        if (!pickerInspector.TryInspect(
            gameController,
            catalogue,
            out var inspection,
            out var inspectionFailure))
        {
            return FailStart($"Click blocked: {inspectionFailure}", out failureReason);
        }

        var target = inspection!.VisibleOptions.FirstOrDefault(
            option => option.Currency.Metadata == queryController.TargetCurrency.Metadata);
        if (target == null)
        {
            return FailStart(
                "Click blocked: the exact metadata target is no longer visible.",
                out failureReason);
        }

        if (Vector2.Distance(target.Center, tweenController.TargetPosition) > 4)
        {
            return FailStart(
                "Click blocked: the target moved after the cursor tween.",
                out failureReason);
        }

        var cursorPosition = Input.MousePositionNum;
        if (!target.Contains(cursorPosition, inset: 2))
        {
            return FailStart(
                $"Click blocked: cursor {cursorPosition.X:0},{cursorPosition.Y:0} is outside " +
                    $"verified option center {target.Center.X:0},{target.Center.Y:0} " +
                    $"size {target.Size.X:0}x{target.Size.Y:0}.",
                out failureReason);
        }

        if (!gameController.Window.IsForeground() || !panel.IsVisible ||
            !panel.CurrencyPicker.IsVisible ||
            panel.CurrencyPicker.IsPickingWantedCurrency !=
                queryController.ExpectedWantedPicker)
        {
            return FailStart(
                "Click blocked: foreground or picker state changed.",
                out failureReason);
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
            return FailStart($"Click failed: {clickFailure.Message}", out failureReason);
        }

        if (releaseFailure != null)
        {
            return FailStart(
                $"Mouse button release failed: {releaseFailure.Message}",
                out failureReason);
        }

        _targetCurrency = queryController.TargetCurrency;
        _expectedWantedPicker = queryController.ExpectedWantedPicker;
        _selectionDeadlineUtc = DateTimeOffset.UtcNow + SelectionTimeout;
        State = VerifiedOptionSelectionState.WaitingForSelection;
        Status = $"Clicked verified {_targetCurrency.Name}; waiting for panel selection.";
        failureReason = string.Empty;
        return true;
    }

    public void Tick(GameController gameController)
    {
        if (!IsRunning)
        {
            return;
        }

        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (!panel.IsVisible)
        {
            Cancel("Selection verification failed: Currency Exchange panel closed.");
            return;
        }

        var selectedItem = _expectedWantedPicker
            ? panel.WantedItemType
            : panel.OfferedItemType;
        if (!panel.CurrencyPicker.IsVisible && selectedItem != null &&
            selectedItem.Metadata == _targetCurrency!.Metadata)
        {
            State = VerifiedOptionSelectionState.Completed;
            Status = $"Verified panel selected {_targetCurrency.Name} after one click.";
            return;
        }

        if (DateTimeOffset.UtcNow >= _selectionDeadlineUtc)
        {
            Cancel($"Selection verification timed out for {_targetCurrency!.Name}; no retry sent.");
        }
    }

    public void Cancel(string reason)
    {
        State = VerifiedOptionSelectionState.Faulted;
        Status = reason;
    }

    private bool FailStart(string reason, out string failureReason)
    {
        Cancel(reason);
        failureReason = reason;
        return false;
    }
}
