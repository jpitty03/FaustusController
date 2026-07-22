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

public enum CurrencySearchQueryFailureKind
{
    None,
    NoExactMetadataMatch,
    Automation
}

public sealed class CurrencySearchQueryController
{
    private readonly record struct QueryKeyStroke(Keys Key, bool Shift);

    private static readonly TimeSpan FocusDelay = TimeSpan.FromMilliseconds(75);
    private static readonly TimeSpan TriggerPollDelay = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan TriggerSettleDelay = TimeSpan.FromMilliseconds(75);
    private static readonly TimeSpan SelectDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan CharacterDelay = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan FilterPollDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan FilterTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(6);

    private CurrencyIdentity? _targetCurrency;
    private IReadOnlyList<QueryKeyStroke> _queryKeys = [];
    private int _queryIndex;
    private bool _isPickingWantedCurrency;
    private Keys _triggerKey;
    private DateTimeOffset _nextActionAtUtc;
    private DateTimeOffset _filterDeadlineUtc;
    private DateTimeOffset _operationDeadlineUtc;

    public CurrencySearchQueryState State { get; private set; } = CurrencySearchQueryState.Idle;
    public string Query { get; private set; } = "";
    public string Status { get; private set; } = "Automatic query input is idle.";
    public CurrencySearchQueryFailureKind FailureKind { get; private set; }
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
        CurrencyCatalogue catalogue,
        out string failureReason)
    {
        return StartCore(
            targetCurrency,
            isPickingWantedCurrency,
            triggerKey,
            catalogue,
            waitForTriggerRelease: true,
            useQuotedFullName: false,
            out failureReason);
    }

    public bool StartAutomated(
        CurrencyIdentity targetCurrency,
        bool isPickingWantedCurrency,
        CurrencyCatalogue catalogue,
        out string failureReason)
    {
        return StartCore(
            targetCurrency,
            isPickingWantedCurrency,
            Keys.None,
            catalogue,
            waitForTriggerRelease: false,
            useQuotedFullName: false,
            out failureReason);
    }

    public bool StartAutomatedQuotedFullName(
        CurrencyIdentity targetCurrency,
        bool isPickingWantedCurrency,
        CurrencyCatalogue catalogue,
        out string failureReason)
    {
        return StartCore(
            targetCurrency,
            isPickingWantedCurrency,
            Keys.None,
            catalogue,
            waitForTriggerRelease: false,
            useQuotedFullName: true,
            out failureReason);
    }

    private bool StartCore(
        CurrencyIdentity targetCurrency,
        bool isPickingWantedCurrency,
        Keys triggerKey,
        CurrencyCatalogue catalogue,
        bool waitForTriggerRelease,
        bool useQuotedFullName,
        out string failureReason)
    {
        if (IsRunning)
        {
            failureReason = "A search query operation is already running.";
            Status = failureReason;
            return false;
        }

        FailureKind = CurrencySearchQueryFailureKind.None;
        Query = useQuotedFullName
            ? CreateQuotedSearchQuery(targetCurrency)
            : CreateSearchQuery(targetCurrency, catalogue);
        if ((waitForTriggerRelease && triggerKey == Keys.None) || Query.Length == 0 ||
            !TryEncodeQuery(Query, out _queryKeys))
        {
            failureReason = $"Could not create a keyboard-safe query for {targetCurrency.Name}.";
            State = CurrencySearchQueryState.Faulted;
            FailureKind = CurrencySearchQueryFailureKind.Automation;
            Status = failureReason;
            return false;
        }

        _targetCurrency = targetCurrency;
        VerifiedTarget = null;
        _isPickingWantedCurrency = isPickingWantedCurrency;
        _triggerKey = triggerKey;
        _queryIndex = 0;
        _nextActionAtUtc = DateTimeOffset.UtcNow;
        var queryDuration = TimeSpan.FromMilliseconds(
            CharacterDelay.TotalMilliseconds * _queryKeys.Count);
        var calculatedTimeout = TriggerSettleDelay + FocusDelay + SelectDelay +
            queryDuration + FilterTimeout + TimeSpan.FromSeconds(1);
        _operationDeadlineUtc = _nextActionAtUtc +
            (calculatedTimeout > OperationTimeout ? calculatedTimeout : OperationTimeout);
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
        FailureKind = CurrencySearchQueryFailureKind.Automation;
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

                    var keyStroke = _queryKeys[_queryIndex];
                    string keyFailure;
                    var sent = keyStroke.Shift
                        ? TrySendChord(
                            gameController,
                            Keys.ShiftKey,
                            keyStroke.Key,
                            out keyFailure)
                        : TrySendKey(keyStroke.Key, out keyFailure);
                    if (!sent)
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
                    Fail(
                        $"Query '{Query}' did not produce a visible exact metadata match.",
                        CurrencySearchQueryFailureKind.NoExactMetadataMatch);
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

    internal static string CreateSearchQuery(
        CurrencyIdentity targetCurrency,
        CurrencyCatalogue catalogue)
    {
        var tokens = Tokenize(targetCurrency.Name);
        if (tokens.Count == 0)
        {
            return "";
        }

        var normalizedCatalogueNames = catalogue.Items
            .Select(currency => string.Join(' ', Tokenize(currency.Name)))
            .ToArray();
        var candidates = new List<(string Query, int MatchCount, bool WeakBoundary)>();
        for (var wordCount = 1; wordCount <= tokens.Count; wordCount++)
        {
            for (var start = 0; start + wordCount <= tokens.Count; start++)
            {
                if (wordCount == 1 && IsWeakStandaloneToken(tokens[start]))
                {
                    continue;
                }

                var query = string.Join(' ', tokens.Skip(start).Take(wordCount));
                var matchCount = normalizedCatalogueNames.Count(name =>
                    name.Contains(query, StringComparison.Ordinal));
                var weakBoundary = wordCount > 1 &&
                    (IsWeakStandaloneToken(tokens[start]) ||
                        IsWeakStandaloneToken(tokens[start + wordCount - 1]));
                candidates.Add((query, matchCount, weakBoundary));
            }
        }

        var unique = candidates
            .Where(candidate => candidate.MatchCount == 1)
            .OrderBy(candidate => candidate.WeakBoundary)
            .ThenBy(candidate => candidate.Query.Length)
            .ThenBy(candidate => candidate.Query, StringComparer.Ordinal)
            .FirstOrDefault();
        if (unique.MatchCount == 1)
        {
            return unique.Query;
        }

        return candidates
            .Where(candidate => candidate.MatchCount > 0)
            .OrderBy(candidate => candidate.MatchCount)
            .ThenBy(candidate => candidate.WeakBoundary)
            .ThenByDescending(candidate => candidate.Query.Count(character => character == ' '))
            .ThenByDescending(candidate => candidate.Query.Length)
            .ThenBy(candidate => candidate.Query, StringComparer.Ordinal)
            .Select(candidate => candidate.Query)
            .FirstOrDefault() ?? string.Join(' ', tokens);
    }

    internal static string CreateQuotedSearchQuery(CurrencyIdentity targetCurrency)
    {
        var normalizedName = string.Join(' ', Tokenize(targetCurrency.Name));
        return normalizedName.Length == 0 ? "" : $"\"{normalizedName}\"";
    }

    private static bool IsWeakStandaloneToken(string token)
    {
        return token.Length < 3 || IsStopword(token);
    }

    private static bool IsStopword(string token)
    {
        return token is
            "and" or "for" or "from" or "of" or "or" or "the" or "to" or "with";
    }

    private static IReadOnlyList<string> Tokenize(string currencyName)
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

        return tokens;
    }

    private static bool TryEncodeQuery(
        string query,
        out IReadOnlyList<QueryKeyStroke> keys)
    {
        var encoded = new List<QueryKeyStroke>(query.Length);
        foreach (var character in query)
        {
            if (character is >= 'a' and <= 'z')
            {
                encoded.Add(new QueryKeyStroke(
                    (Keys)((int)Keys.A + character - 'a'),
                    false));
            }
            else if (character is >= '0' and <= '9')
            {
                encoded.Add(new QueryKeyStroke(
                    (Keys)((int)Keys.D0 + character - '0'),
                    false));
            }
            else if (character == ' ')
            {
                encoded.Add(new QueryKeyStroke(Keys.Space, false));
            }
            else if (character == '"')
            {
                encoded.Add(new QueryKeyStroke(Keys.OemQuotes, true));
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

    private void Fail(
        string reason,
        CurrencySearchQueryFailureKind failureKind = CurrencySearchQueryFailureKind.Automation)
    {
        State = CurrencySearchQueryState.Faulted;
        VerifiedTarget = null;
        FailureKind = failureKind;
        Status = reason;
    }
}
