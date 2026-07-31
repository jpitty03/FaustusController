namespace FaustusController;

/// <summary>
/// How a route hop is intended to be executed against the currency exchange. This is
/// distinct from <c>BookSide</c>, which is the provenance of the quote (which book the
/// rate was read from). A single directed pair can carry one edge per mode:
/// <list type="bullet">
/// <item><see cref="Immediate"/> — take a resting listing on the book right now; the
/// trade fills on placement (subject to listed depth).</item>
/// <item><see cref="RestingLimit"/> — post a resting limit order at the competing-book
/// ratio and wait in the queue; no immediately fillable liquidity is assumed.</item>
/// </list>
/// </summary>
public enum ExecutionMode
{
    Immediate,
    RestingLimit
}

/// <summary>
/// Canonical persisted string forms of <see cref="ExecutionMode"/>. Persisted graph,
/// route, plan, and audit files store these strings (like <c>BookSide</c>), never the
/// enum's numeric value, so file compatibility never depends on enum ordering.
/// </summary>
public static class ExecutionModes
{
    public const string Immediate = "Immediate";
    public const string RestingLimit = "RestingLimit";

    public static bool IsValid(string? mode) =>
        mode is Immediate or RestingLimit;

    public static string ToPersisted(ExecutionMode mode) =>
        mode == ExecutionMode.RestingLimit ? RestingLimit : Immediate;

    public static bool TryParse(string? mode, out ExecutionMode parsed)
    {
        switch (mode)
        {
            case Immediate:
                parsed = ExecutionMode.Immediate;
                return true;
            case RestingLimit:
                parsed = ExecutionMode.RestingLimit;
                return true;
            default:
                parsed = ExecutionMode.Immediate;
                return false;
        }
    }
}
