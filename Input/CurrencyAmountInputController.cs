using ExileCore;
using ExileCore.PoEMemory;
using System.Globalization;
using System.Numerics;
using System.Windows.Forms;

namespace FaustusController;

public enum CurrencyAmountInputState
{
    Idle,
    WaitForTriggerRelease,
    MovingToInput,
    ReadyToClick,
    WaitForFocus,
    SelectExistingAmount,
    EnterDigits,
    VerifyAmount,
    Completed,
    Faulted
}

public sealed class CurrencyAmountInputController
{
    private const float ManualInterruptionDistance = 25;
    private const float TargetMovementTolerance = 4;
    private const float ClickPositionTolerance = 6;
    private const long MaximumAmount = 999_999_999;
    private const int TextSearchDepth = 3;
    private static readonly TimeSpan MinimumDuration = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan MaximumDuration = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan TriggerPollDelay = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan TriggerSettleDelay = TimeSpan.FromMilliseconds(75);
    private static readonly TimeSpan FocusPollDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan FocusTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SelectDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan CharacterDelay = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan VerifyPollDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(8);

    private bool _wantedInput;
    private string _digits = "";
    private int _digitIndex;
    private Keys _triggerKey;
    private int _pixelsPerSecond;
    private Vector2 _start;
    private Vector2 _target;
    private Vector2 _control1;
    private Vector2 _control2;
    private Vector2 _lastCommandedPosition;
    private DateTimeOffset _movementStartedAtUtc;
    private TimeSpan _duration;
    private float _curveDirection = 1;
    private DateTimeOffset _nextActionAtUtc;
    private DateTimeOffset _focusDeadlineUtc;
    private DateTimeOffset _verifyDeadlineUtc;
    private DateTimeOffset _operationDeadlineUtc;

    public CurrencyAmountInputState State { get; private set; } = CurrencyAmountInputState.Idle;
    public string Status { get; private set; } =
        "Order amount input is disabled by default.";
    public long Amount { get; private set; }
    public bool WantedInput => _wantedInput;
    public bool IsRunning => State is not CurrencyAmountInputState.Idle and
        not CurrencyAmountInputState.Completed and
        not CurrencyAmountInputState.Faulted;

    public bool Start(
        GameController gameController,
        bool wantedInput,
        long amount,
        Keys triggerKey,
        int pixelsPerSecond,
        out string failureReason)
    {
        return StartCore(
            gameController,
            wantedInput,
            amount,
            triggerKey,
            pixelsPerSecond,
            waitForTriggerRelease: true,
            out failureReason);
    }

    public bool StartAutomated(
        GameController gameController,
        bool wantedInput,
        long amount,
        int pixelsPerSecond,
        out string failureReason)
    {
        return StartCore(
            gameController,
            wantedInput,
            amount,
            Keys.None,
            pixelsPerSecond,
            waitForTriggerRelease: false,
            out failureReason);
    }

    public void Cancel(string reason)
    {
        State = CurrencyAmountInputState.Faulted;
        Status = reason;
    }

    public void Tick(GameController gameController)
    {
        if (!IsRunning)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now > _operationDeadlineUtc)
        {
            Cancel($"Amount input timed out entering {Amount} into {SideName}.");
            return;
        }

        if (now < _nextActionAtUtc)
        {
            return;
        }

        switch (State)
        {
            case CurrencyAmountInputState.WaitForTriggerRelease:
                TickTriggerRelease(gameController, now);
                return;
            case CurrencyAmountInputState.MovingToInput:
                TickMovement(gameController);
                return;
            case CurrencyAmountInputState.ReadyToClick:
                ClickInput(gameController, now);
                return;
            case CurrencyAmountInputState.WaitForFocus:
                TickFocusWait(gameController, now);
                return;
            case CurrencyAmountInputState.SelectExistingAmount:
                TickSelectExisting(gameController, now);
                return;
            case CurrencyAmountInputState.EnterDigits:
                TickEnterDigits(gameController, now);
                return;
            case CurrencyAmountInputState.VerifyAmount:
                TickVerify(gameController, now);
                return;
        }
    }

    private string SideName => _wantedInput ? "the I want count input" : "the I have count input";

    private bool StartCore(
        GameController gameController,
        bool wantedInput,
        long amount,
        Keys triggerKey,
        int pixelsPerSecond,
        bool waitForTriggerRelease,
        out string failureReason)
    {
        if (IsRunning)
        {
            return FailStart("An amount input operation is already running.", out failureReason);
        }

        if (waitForTriggerRelease && triggerKey == Keys.None)
        {
            return FailStart(
                "Amount input blocked: bind a keyboard key to the amount hotkey first.",
                out failureReason);
        }

        if (amount <= 0 || amount > MaximumAmount)
        {
            return FailStart(
                $"Amount input blocked: {amount} is outside 1..{MaximumAmount}.",
                out failureReason);
        }

        _wantedInput = wantedInput;
        Amount = amount;
        if (!TryValidateContext(gameController, requireFocus: false, out _, out var contextFailure))
        {
            return FailStart(contextFailure, out failureReason);
        }

        _digits = amount.ToString(CultureInfo.InvariantCulture);
        _digitIndex = 0;
        _triggerKey = triggerKey;
        _pixelsPerSecond = pixelsPerSecond;
        _movementStartedAtUtc = default;
        _nextActionAtUtc = DateTimeOffset.UtcNow;
        var typingDuration = TimeSpan.FromMilliseconds(
            CharacterDelay.TotalMilliseconds * _digits.Length);
        var calculatedTimeout = TriggerSettleDelay + MaximumDuration + FocusTimeout +
            SelectDelay + typingDuration + VerifyTimeout + TimeSpan.FromSeconds(1);
        _operationDeadlineUtc = _nextActionAtUtc +
            (calculatedTimeout > OperationTimeout ? calculatedTimeout : OperationTimeout);
        State = waitForTriggerRelease
            ? CurrencyAmountInputState.WaitForTriggerRelease
            : CurrencyAmountInputState.MovingToInput;
        if (!waitForTriggerRelease)
        {
            _nextActionAtUtc += TriggerSettleDelay;
        }

        Status = waitForTriggerRelease
            ? $"Waiting for hotkey release before entering {Amount} into {SideName}."
            : $"Automated amount {Amount} queued for {SideName}.";
        failureReason = string.Empty;
        return true;
    }

    private void TickTriggerRelease(GameController gameController, DateTimeOffset now)
    {
        if (!TryValidateContext(gameController, requireFocus: false, out _, out var contextFailure))
        {
            Cancel(contextFailure);
            return;
        }

        if (Input.IsKeyDown(_triggerKey))
        {
            Status = $"Waiting for {_triggerKey} release before entering {Amount} into {SideName}.";
            _nextActionAtUtc = now + TriggerPollDelay;
            return;
        }

        State = CurrencyAmountInputState.MovingToInput;
        Status = $"Hotkey released; moving to {SideName}.";
        _nextActionAtUtc = now + TriggerSettleDelay;
    }

    private void TickMovement(GameController gameController)
    {
        if (!TryValidateContext(
            gameController,
            requireFocus: false,
            out var freshTarget,
            out var contextFailure))
        {
            Cancel(contextFailure);
            return;
        }

        if (_movementStartedAtUtc == default)
        {
            BeginMovement(freshTarget);
            if (State != CurrencyAmountInputState.MovingToInput)
            {
                return;
            }
        }

        if (Vector2.Distance(freshTarget, _target) > TargetMovementTolerance)
        {
            Cancel($"Amount input cancelled: {SideName} moved during tween.");
            return;
        }

        if (Vector2.Distance(Input.MousePositionNum, _lastCommandedPosition) >
            ManualInterruptionDistance)
        {
            Cancel("Amount input cancelled: manual mouse movement detected.");
            return;
        }

        var elapsed = DateTimeOffset.UtcNow - _movementStartedAtUtc;
        var progress = Math.Clamp(
            (float)(elapsed.TotalMilliseconds / _duration.TotalMilliseconds),
            0,
            1);
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
            Cancel($"Amount input tween failed: {exception.Message}");
            return;
        }

        if (progress >= 1)
        {
            State = CurrencyAmountInputState.ReadyToClick;
            Status = $"Reached {SideName}; revalidating before one click.";
        }
    }

    private void BeginMovement(Vector2 target)
    {
        _start = Input.MousePositionNum;
        _target = target;
        _lastCommandedPosition = _start;
        var delta = _target - _start;
        var distance = delta.Length();
        if (distance < 1)
        {
            State = CurrencyAmountInputState.ReadyToClick;
            Status = $"Cursor already at {SideName}; preparing one click.";
            return;
        }

        _duration = TimeSpan.FromSeconds(distance / Math.Max(_pixelsPerSecond, 1));
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
        _movementStartedAtUtc = DateTimeOffset.UtcNow;
        Status = $"Tweening to {SideName} over {_duration.TotalMilliseconds:0}ms.";
    }

    private void ClickInput(GameController gameController, DateTimeOffset now)
    {
        if (!TryValidateContext(
            gameController,
            requireFocus: false,
            out var freshTarget,
            out var contextFailure))
        {
            Cancel(contextFailure);
            return;
        }

        if (Vector2.Distance(freshTarget, _target) > TargetMovementTolerance ||
            Vector2.Distance(Input.MousePositionNum, freshTarget) > ClickPositionTolerance)
        {
            Cancel($"Amount input cancelled: cursor or {SideName} moved before click.");
            return;
        }

        if (AreModifiersDown())
        {
            Cancel("Amount input cancelled: release Ctrl, Shift, and Alt before the click.");
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
            Cancel($"Amount input click failed: {clickFailure.Message}");
            return;
        }

        if (releaseFailure != null)
        {
            Cancel($"Amount input click release failed: {releaseFailure.Message}");
            return;
        }

        _focusDeadlineUtc = now + FocusTimeout;
        State = CurrencyAmountInputState.WaitForFocus;
        Status = $"Clicked {SideName}; waiting for the input to become active.";
        _nextActionAtUtc = now + FocusPollDelay;
    }

    private void TickFocusWait(GameController gameController, DateTimeOffset now)
    {
        if (!TryValidateContext(
            gameController,
            requireFocus: false,
            out _,
            out var contextFailure,
            out var input))
        {
            Cancel(contextFailure);
            return;
        }

        if (input!.IsActive)
        {
            State = CurrencyAmountInputState.SelectExistingAmount;
            Status = $"{SideName} is active; selecting the existing amount.";
            _nextActionAtUtc = now + SelectDelay;
            return;
        }

        if (now >= _focusDeadlineUtc)
        {
            Cancel($"Amount input failed: {SideName} did not activate after one click; no retry sent.");
            return;
        }

        Status = $"Waiting for {SideName} to become active after the click.";
        _nextActionAtUtc = now + FocusPollDelay;
    }

    private void TickSelectExisting(GameController gameController, DateTimeOffset now)
    {
        if (AreModifiersDown())
        {
            Cancel("Amount input cancelled: release Ctrl, Shift, and Alt before typing.");
            return;
        }

        if (!TrySendChord(gameController, Keys.ControlKey, Keys.A, out var selectFailure))
        {
            Cancel($"Ctrl+A failed: {selectFailure}");
            return;
        }

        State = CurrencyAmountInputState.EnterDigits;
        Status = $"Entering {Amount} into {SideName}.";
        _nextActionAtUtc = now + SelectDelay;
    }

    private void TickEnterDigits(GameController gameController, DateTimeOffset now)
    {
        if (!TryValidateContext(gameController, requireFocus: true, out _, out var contextFailure))
        {
            Cancel(contextFailure);
            return;
        }

        if (_digitIndex < _digits.Length)
        {
            if (AreModifiersDown())
            {
                Cancel("Amount input cancelled because Ctrl, Shift, or Alt is held.");
                return;
            }

            var key = Keys.D0 + (_digits[_digitIndex] - '0');
            if (!TrySendKey(key, out var keyFailure))
            {
                Cancel($"Amount input failed at digit {_digitIndex + 1}: {keyFailure}");
                return;
            }

            _digitIndex++;
            Status = $"Entering {Amount} into {SideName}: {_digitIndex}/{_digits.Length}.";
            _nextActionAtUtc = now + CharacterDelay;
            return;
        }

        State = CurrencyAmountInputState.VerifyAmount;
        _verifyDeadlineUtc = now + VerifyTimeout;
        Status = $"Typed {Amount}; verifying {SideName} text.";
        _nextActionAtUtc = now + VerifyPollDelay;
    }

    private void TickVerify(GameController gameController, DateTimeOffset now)
    {
        if (!TryValidateContext(
            gameController,
            requireFocus: false,
            out _,
            out var contextFailure,
            out var input))
        {
            Cancel(contextFailure);
            return;
        }

        var readDigits = ReadDigits(input!);
        if (readDigits == _digits)
        {
            State = CurrencyAmountInputState.Completed;
            Status = $"Verified {Amount} in {SideName}.";
            return;
        }

        if (now >= _verifyDeadlineUtc)
        {
            Cancel(
                $"Amount verification failed: {SideName} shows " +
                    $"'{(readDigits.Length == 0 ? "no digits" : readDigits)}' instead of {Amount}.");
            return;
        }

        Status = $"Waiting for {SideName} to show {Amount}.";
        _nextActionAtUtc = now + VerifyPollDelay;
    }

    private bool TryValidateContext(
        GameController gameController,
        bool requireFocus,
        out Vector2 targetPosition,
        out string failureReason)
    {
        return TryValidateContext(
            gameController,
            requireFocus,
            out targetPosition,
            out failureReason,
            out _);
    }

    private bool TryValidateContext(
        GameController gameController,
        bool requireFocus,
        out Vector2 targetPosition,
        out string failureReason,
        out Element? input)
    {
        targetPosition = default;
        input = null;
        if (!gameController.Window.IsForeground())
        {
            failureReason = "Amount input cancelled: Path of Exile is not foreground.";
            return false;
        }

        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (!panel.IsVisible)
        {
            failureReason = "Amount input cancelled: the Currency Exchange panel is not visible.";
            return false;
        }

        if (panel.CurrencyPicker.IsVisible)
        {
            failureReason = "Amount input cancelled: close the currency picker first.";
            return false;
        }

        input = _wantedInput ? panel.WantedItemCountInput : panel.OfferedItemCountInput;
        if (input is not { IsVisible: true })
        {
            failureReason = $"Amount input cancelled: {SideName} is not visible.";
            return false;
        }

        if (requireFocus && !input.IsActive)
        {
            failureReason = $"Amount input cancelled: {SideName} lost focus.";
            return false;
        }

        var rectangle = input.GetClientRectCache;
        targetPosition = new Vector2(
            rectangle.X + rectangle.Width / 2,
            rectangle.Y + rectangle.Height / 2);
        failureReason = string.Empty;
        return true;
    }

    private static string ReadDigits(Element input)
    {
        var text = FindText(input, TextSearchDepth) ?? "";
        return new string(text.Where(char.IsAsciiDigit).ToArray());
    }

    private static string? FindText(Element element, int remainingDepth)
    {
        var text = element.Text;
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        if (remainingDepth <= 0)
        {
            return null;
        }

        var children = element.Children;
        if (children == null)
        {
            return null;
        }

        foreach (var child in children)
        {
            if (child == null || !child.IsVisible)
            {
                continue;
            }

            var childText = FindText(child, remainingDepth - 1);
            if (!string.IsNullOrWhiteSpace(childText))
            {
                return childText;
            }
        }

        return null;
    }

    private bool TrySendChord(
        GameController gameController,
        Keys modifier,
        Keys key,
        out string failureReason)
    {
        var modifierDown = false;
        var keyDown = false;
        string? contextFailure = null;
        Exception? operationFailure = null;
        Exception? releaseFailure = null;
        try
        {
            modifierDown = true;
            Input.KeyDown(modifier);
            if (!TryValidateContext(
                gameController,
                requireFocus: true,
                out _,
                out var validationFailure))
            {
                contextFailure = validationFailure;
            }
            else
            {
                keyDown = true;
                Input.KeyDown(key);
            }
        }
        catch (Exception exception)
        {
            operationFailure = exception;
        }
        finally
        {
            if (keyDown)
            {
                try
                {
                    Input.KeyUp(key);
                }
                catch (Exception exception)
                {
                    releaseFailure = exception;
                }
            }

            if (modifierDown)
            {
                try
                {
                    Input.KeyUp(modifier);
                }
                catch (Exception exception)
                {
                    releaseFailure ??= exception;
                }
            }
        }

        if (contextFailure != null)
        {
            failureReason = contextFailure;
            return false;
        }

        if (operationFailure != null)
        {
            failureReason = operationFailure.Message;
            return false;
        }

        if (releaseFailure != null)
        {
            failureReason = $"Key release failed: {releaseFailure.Message}";
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    private static bool TrySendKey(Keys key, out string failureReason)
    {
        var keyDown = false;
        Exception? operationFailure = null;
        Exception? releaseFailure = null;
        try
        {
            keyDown = true;
            Input.KeyDown(key);
        }
        catch (Exception exception)
        {
            operationFailure = exception;
        }
        finally
        {
            if (keyDown)
            {
                try
                {
                    Input.KeyUp(key);
                }
                catch (Exception exception)
                {
                    releaseFailure = exception;
                }
            }
        }

        if (operationFailure != null)
        {
            failureReason = operationFailure.Message;
            return false;
        }

        if (releaseFailure != null)
        {
            failureReason = $"Key release failed: {releaseFailure.Message}";
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    private static bool AreModifiersDown()
    {
        return Input.IsKeyDown(Keys.ControlKey) || Input.IsKeyDown(Keys.ShiftKey) ||
            Input.IsKeyDown(Keys.Menu);
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
