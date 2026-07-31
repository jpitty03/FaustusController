using System.Numerics;

namespace FaustusController;

/// <summary>
/// Pure, overflow-safe exact arithmetic shared by the staging, execution, and placement
/// controllers (and exercised directly by the pure-check harness). No game or SDK types:
/// every method is a deterministic function of integers and rational rates, so a resting
/// order's exact-lot sizing, favorable-drift check, slippage floor, and placed-order ratio
/// match are all verifiable without a running client.
/// </summary>
public static class OrderExecutionMath
{
    /// <summary>
    /// Sizes a resting order to the largest whole number of live quote lots whose offered
    /// amount does not exceed the planned spend. The order uses the exact reduced live
    /// competing ratio (<paramref name="getUnits"/>:<paramref name="giveUnits"/>); the
    /// remainder of the planned spend is left uncommitted.
    /// </summary>
    public static bool TryComputeRestingLots(
        long plannedSpent,
        int getUnits,
        int giveUnits,
        out long lots,
        out long offered,
        out long wanted,
        out long uncommittedRemainder,
        out string failureReason)
    {
        lots = 0;
        offered = 0;
        wanted = 0;
        uncommittedRemainder = 0;
        if (plannedSpent <= 0 || getUnits <= 0 || giveUnits <= 0)
        {
            failureReason = "resting order sizing needs a positive planned spend and live ratio.";
            return false;
        }

        lots = plannedSpent / giveUnits;
        if (lots <= 0)
        {
            failureReason = $"the planned spend {plannedSpent} is smaller than one live lot " +
                $"({giveUnits} offered per lot); no whole resting lot fits.";
            return false;
        }

        try
        {
            offered = checked(lots * giveUnits);
            wanted = checked(lots * getUnits);
        }
        catch (OverflowException)
        {
            lots = 0;
            offered = 0;
            wanted = 0;
            failureReason = "resting order sizing overflowed.";
            return false;
        }

        uncommittedRemainder = plannedSpent - offered;
        failureReason = string.Empty;
        return true;
    }

    /// <summary>
    /// A typed resting order stays at least as competitive as the final competing head when
    /// its wanted-per-offered price is no greater than the head's:
    /// <c>typedWanted * finalGive &lt;= typedOffered * finalGet</c>. Exact, overflow-safe.
    /// A favorable head move (head offering more) still passes; an adverse one fails.
    /// </summary>
    public static bool IsAtLeastAsCompetitiveAsHead(
        long typedOffered,
        long typedWanted,
        int finalGetUnits,
        int finalGiveUnits)
    {
        if (typedOffered <= 0 || typedWanted <= 0 ||
            finalGetUnits <= 0 || finalGiveUnits <= 0)
        {
            return false;
        }

        var left = (BigInteger)typedWanted * finalGiveUnits;
        var right = (BigInteger)typedOffered * finalGetUnits;
        return left <= right;
    }

    /// <summary>
    /// The staged order's actual typed ratio must clear the planned rate less the allowed
    /// integer slippage percent:
    /// <c>stagedWanted * plannedGive * 100 &gt;= stagedOffered * plannedGet * (100 - slippagePercent)</c>.
    /// Equality passes. Exact, overflow-safe. This gates the real typed amounts, not just the
    /// book head, so floor rounding can never place a ratio below the advertised floor.
    /// </summary>
    public static bool PassesSlippageFloor(
        long stagedOffered,
        long stagedWanted,
        int plannedGetUnits,
        int plannedGiveUnits,
        int slippagePercent)
    {
        if (stagedOffered <= 0 || stagedWanted <= 0 ||
            plannedGetUnits <= 0 || plannedGiveUnits <= 0 ||
            slippagePercent is < 0 or > 100)
        {
            return false;
        }

        var left = (BigInteger)stagedWanted * plannedGiveUnits * 100;
        var right = (BigInteger)stagedOffered * plannedGetUnits * (100 - slippagePercent);
        return left >= right;
    }

    /// <summary>
    /// Two offered:wanted ratios are equivalent by cross multiplication, tolerant of either
    /// side being unreduced. All parts must be positive.
    /// </summary>
    public static bool RatiosEquivalent(
        long leftOffered,
        long leftWanted,
        long rightOffered,
        long rightWanted)
    {
        if (leftOffered <= 0 || leftWanted <= 0 || rightOffered <= 0 || rightWanted <= 0)
        {
            return false;
        }

        return (BigInteger)leftOffered * rightWanted == (BigInteger)rightOffered * leftWanted;
    }
}
