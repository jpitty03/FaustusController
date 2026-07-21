using ExileCore;
using System.Text;
using System.Windows.Forms;

namespace FaustusController;

public enum CurrencySearchQueryState
{
    Idle,
    WaitForTriggerRelease,
    FocusSearch,
    SelectExistingQuery,
    EnterQuery,
    WaitForFilteredOption,
    Completed,
    Faulted
}

public sealed class CurrencySearchQueryController
{
    private static readonly TimeSpan FocusDelay = TimeSpan.FromMilliseconds(75);
    private static readonly TimeSpan TriggerPollDelay = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan TriggerSettleDelay = TimeSpan.FromMilliseconds(75);
    private static readonly TimeSpan SelectDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan CharacterDelay = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan FilterPollDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan FilterTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(6);

    private CurrencyIdentity? _targetCurrency;
    private IReadOnlyList<Keys> _queryKeys = [];
    private int _queryIndex;
    private bool _isPickingWantedCurrency;
    private Keys _triggerKey;
    private DateTimeOffset _nextActionAtUtc;
    private DateTimeOffset _filterDeadlineUtc;
    private DateTimeOffset _operationDeadlineUtc;

    public CurrencySearchQueryState State { get; private set; } = CurrencySearchQueryState.Idle;
    public string Query { get; private set; } = "";
    public string Status { get; private set; } = "Automatic query input is idle.";
    public CurrencyIdentity? TargetCurrency => _targetCurrency;
    public bool ExpectedWantedPicker => _isPickingWantedCurrency;
    public CurrencyPickerOptionTarget? VerifiedTarget { get; private set; }
    public bool IsRunning => State is not CurrencySearchQueryState.Idle and
        not CurrencySearchQueryState.Completed and
        not CurrencySearchQueryState.Faulted;

    public bool Start(
        CurrencyIdentity targetCurrency,
        bool isPickingWantedCurrency,
        Keys triggerKey,
        out string failureReason)
    {
        return StartCore(
            targetCurrency,
            isPickingWantedCurrency,
            triggerKey,
            waitForTriggerRelease: true,
            out failureReason);
    }

    public bool StartAutomated(
        CurrencyIdentity targetCurrency,
        bool isPickingWantedCurrency,
        out string failureReason)
    {
        return StartCore(
            targetCurrency,
            isPickingWantedCurrency,
            Keys.None,
            waitForTriggerRelease: false,
            out failureReason);
    }

    private bool StartCore(
        CurrencyIdentity targetCurrency,
        bool isPickingWantedCurrency,
        Keys triggerKey,
        bool waitForTriggerRelease,
        out string failureReason)
    {
        if (IsRunning)
        {
            failureReason = "A search query operation is already running.";
            Status = failureReason;
            return false;
        }

        Query = CreateSearchToken(targetCurrency.Name);
        if ((waitForTriggerRelease && triggerKey == Keys.None) || Query.Length == 0 ||
            !TryEncodeQuery(Query, out _queryKeys))
        {
            failureReason = $"Could not create a keyboard-safe query for {targetCurrency.Name}.";
            State = CurrencySearchQueryState.Faulted;
            Status = failureReason;
            return false;
        }

        _targetCurrency = targetCurrency;
        VerifiedTarget = null;
        _isPickingWantedCurrency = isPickingWantedCurrency;
        _triggerKey = triggerKey;
        _queryIndex = 0;
        _nextActionAtUtc = DateTimeOffset.UtcNow;
        _operationDeadlineUtc = _nextActionAtUtc + OperationTimeout;
        State = waitForTriggerRelease
            ? CurrencySearchQueryState.WaitForTriggerRelease
            : CurrencySearchQueryState.FocusSearch;
        if (!waitForTriggerRelease)
        {
            _nextActionAtUtc += TriggerSettleDelay;
        }

        Status = waitForTriggerRelease
            ? $"Waiting for query hotkey release before entering '{Query}'."
            : $"Automated query '{Query}' queued after focus settle delay.";
        failureReason = string.Empty;
        return true;
    }

    public void Cancel(string reason)
    {
        State = CurrencySearchQueryState.Faulted;
        VerifiedTarget = null;
        Status = reason;
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

        var now = DateTimeOffset.UtcNow;
        if (now > _operationDeadlineUtc)
        {
            Fail("Search query operation timed out.");
            return;
        }

        if (now < _nextActionAtUtc)
        {
            return;
        }

        if (!TryValidateContext(gameController, out var contextFailure))
        {
            Fail(contextFailure);
            return;
        }

        switch (State)
        {
            case CurrencySearchQueryState.WaitForTriggerRelease:
                if (Input.IsKeyDown(_triggerKey))
                {
                    Status = $"Waiting for {_triggerKey} release before entering '{Query}'.";
                    _nextActionAtUtc = now + TriggerPollDelay;
                    return;
                }

                State = CurrencySearchQueryState.FocusSearch;
                Status = "Query hotkey released; waiting for input focus to settle.";
                _nextActionAtUtc = now + TriggerSettleDelay;
                return;

            case CurrencySearchQueryState.FocusSearch:
                if (AreModifiersDown())
                {
                    Fail("Release Ctrl, Shift, and Alt before starting query input.");
                    return;
                }

                if (!TrySendChord(gameController, Keys.ControlKey, Keys.F, out var focusFailure))
                {
                    Fail($"Ctrl+F failed: {focusFailure}");
                    return;
                }

                State = CurrencySearchQueryState.SelectExistingQuery;
                Status = "Search focused; preparing to replace the existing query.";
                _nextActionAtUtc = now + FocusDelay;
                return;

            case CurrencySearchQueryState.SelectExistingQuery:
                if (!TrySendChord(gameController, Keys.ControlKey, Keys.A, out var selectFailure))
                {
                    Fail($"Ctrl+A failed: {selectFailure}");
                    return;
                }

                State = CurrencySearchQueryState.EnterQuery;
                Status = $"Entering query '{Query}'.";
                _nextActionAtUtc = now + SelectDelay;
                return;

            case CurrencySearchQueryState.EnterQuery:
                if (_queryIndex < _queryKeys.Count)
                {
                    if (AreModifiersDown())
                    {
                        Fail("Search query cancelled because Ctrl, Shift, or Alt is held.");
                        return;
                    }

                    if (!TrySendKey(_queryKeys[_queryIndex], out var keyFailure))
                    {
                        Fail($"Query input failed at character {_queryIndex + 1}: {keyFailure}");
                        return;
                    }

                    _queryIndex++;
                    Status = $"Entering query '{Query}': {_queryIndex}/{_queryKeys.Count}.";
                    _nextActionAtUtc = now + CharacterDelay;
                    return;
                }

                State = CurrencySearchQueryState.WaitForFilteredOption;
                _filterDeadlineUtc = now + FilterTimeout;
                Status = $"Query '{Query}' entered; waiting for exact metadata match.";
                _nextActionAtUtc = now + FilterPollDelay;
                return;

            case CurrencySearchQueryState.WaitForFilteredOption:
                if (!pickerInspector.TryInspect(
                    gameController,
                    catalogue,
                    out var inspection,
                    out var inspectionFailure))
                {
                    Fail(inspectionFailure);
                    return;
                }

                var exactMatch = inspection!.VisibleOptions.FirstOrDefault(
                    option => option.Currency.Metadata == _targetCurrency!.Metadata);
                if (exactMatch != null)
                {
                    VerifiedTarget = exactMatch;
                    State = CurrencySearchQueryState.Completed;
                    Status = $"Verified {exactMatch.Currency.Name} by exact metadata; no click sent.";
                    return;
                }

                if (now >= _filterDeadlineUtc)
                {
                    Fail($"Query '{Query}' did not produce a visible exact metadata match.");
                    return;
                }

                Status = $"Waiting for {_targetCurrency!.Name}; " +
                    $"{inspection.VisibleOptions.Count} options currently visible.";
                _nextActionAtUtc = now + FilterPollDelay;
                return;
        }
    }

    private bool TryValidateContext(GameController gameController, out string failureReason)
    {
        if (!gameController.Window.IsForeground())
        {
            failureReason = "Search query cancelled: Path of Exile is not foreground.";
            return false;
        }

        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (!panel.IsVisible || !panel.CurrencyPicker.IsVisible)
        {
            failureReason = "Search query cancelled: the Currency Exchange picker is not visible.";
            return false;
        }

        if (panel.CurrencyPicker.IsPickingWantedCurrency != _isPickingWantedCurrency)
        {
            failureReason = "Search query cancelled: the active picker side changed.";
            return false;
        }

        failureReason = string.Empty;
        return true;
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
            if (!TryValidateContext(gameController, out var validationFailure))
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
        return Input.IsKeyDown(Keys.ControlKey) ||
            Input.IsKeyDown(Keys.ShiftKey) ||
            Input.IsKeyDown(Keys.Menu);
    }

    private static string CreateSearchToken(string currencyName)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (var character in currencyName.ToLowerInvariant())
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                current.Append(character);
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens
            .OrderByDescending(token => token.Length)
            .ThenBy(token => token, StringComparer.Ordinal)
            .FirstOrDefault() ?? "";
    }

    private static bool TryEncodeQuery(string query, out IReadOnlyList<Keys> keys)
    {
        var encoded = new List<Keys>(query.Length);
        foreach (var character in query)
        {
            if (character is >= 'a' and <= 'z')
            {
                encoded.Add((Keys)((int)Keys.A + character - 'a'));
            }
            else if (character is >= '0' and <= '9')
            {
                encoded.Add((Keys)((int)Keys.D0 + character - '0'));
            }
            else
            {
                keys = [];
                return false;
            }
        }

        keys = encoded;
        return encoded.Count > 0;
    }

    private void Fail(string reason)
    {
        State = CurrencySearchQueryState.Faulted;
        VerifiedTarget = null;
        Status = reason;
    }
}
