namespace FaustusController;

public sealed record CurrencyIdentity
{
    public CurrencyIdentity(string metadata, uint hash, string name)
    {
        if (string.IsNullOrWhiteSpace(metadata))
        {
            throw new ArgumentException("Currency metadata is required.", nameof(metadata));
        }

        Metadata = metadata;
        Hash = hash;
        Name = string.IsNullOrWhiteSpace(name) ? metadata : name;
    }

    public string Metadata { get; }
    public uint Hash { get; }
    public string Name { get; }
}

public readonly record struct CurrencyPairKey(string OfferedMetadata, string WantedMetadata);

public readonly record struct WholeUnitConversion(
    long OfferedUnits,
    long WantedUnits,
    long UnusedOfferedUnits,
    long BatchCount);

public sealed record RationalExchangeRate
{
    private RationalExchangeRate(int rawGet, int rawGive, int getUnits, int giveUnits)
    {
        RawGet = rawGet;
        RawGive = rawGive;
        GetUnits = getUnits;
        GiveUnits = giveUnits;
    }

    public int RawGet { get; }
    public int RawGive { get; }
    public int GetUnits { get; }
    public int GiveUnits { get; }
    public decimal WantedPerOffered => (decimal)GetUnits / GiveUnits;

    public static bool TryCreate(int get, int give, out RationalExchangeRate? rate)
    {
        if (get <= 0 || give <= 0)
        {
            rate = null;
            return false;
        }

        var divisor = GreatestCommonDivisor(get, give);
        rate = new RationalExchangeRate(get, give, get / divisor, give / divisor);
        return true;
    }

    public bool TryConvertWhole(long offeredUnits, out WholeUnitConversion conversion)
    {
        if (offeredUnits < 0)
        {
            conversion = default;
            return false;
        }

        var batchCount = offeredUnits / GiveUnits;

        try
        {
            var spent = checked(batchCount * GiveUnits);
            var received = checked(batchCount * GetUnits);
            conversion = new WholeUnitConversion(
                spent,
                received,
                offeredUnits - spent,
                batchCount);
            return true;
        }
        catch (OverflowException)
        {
            conversion = default;
            return false;
        }
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return left;
    }
}

public enum ExchangeStockSide
{
    WantedItem,
    OfferedItem
}

public sealed record ExchangeStockLevel
{
    private ExchangeStockLevel(
        ExchangeStockSide side,
        int rawGet,
        int rawGive,
        int listedCount,
        RationalExchangeRate rawRate,
        RationalExchangeRate selectedPairRate)
    {
        Side = side;
        RawGet = rawGet;
        RawGive = rawGive;
        ListedCount = listedCount;
        RawRate = rawRate;
        SelectedPairRate = selectedPairRate;
    }

    public ExchangeStockSide Side { get; }
    public int RawGet { get; }
    public int RawGive { get; }
    public int ListedCount { get; }
    public RationalExchangeRate RawRate { get; }
    public RationalExchangeRate SelectedPairRate { get; }

    public static bool TryCreate(
        ExchangeStockSide side,
        int rawGet,
        int rawGive,
        int listedCount,
        out ExchangeStockLevel? level)
    {
        var selectedGet = side == ExchangeStockSide.OfferedItem ? rawGive : rawGet;
        var selectedGive = side == ExchangeStockSide.OfferedItem ? rawGet : rawGive;
        if (!RationalExchangeRate.TryCreate(rawGet, rawGive, out var rawRate) ||
            !RationalExchangeRate.TryCreate(selectedGet, selectedGive, out var selectedPairRate))
        {
            level = null;
            return false;
        }

        level = new ExchangeStockLevel(
            side,
            rawGet,
            rawGive,
            listedCount,
            rawRate!,
            selectedPairRate!);
        return true;
    }
}

public sealed record ExchangePairSnapshot(
    DateTimeOffset CapturedAtUtc,
    CurrencyIdentity OfferedCurrency,
    CurrencyIdentity WantedCurrency,
    RationalExchangeRate? MarketRate,
    IReadOnlyList<ExchangeStockLevel> WantedItemStock,
    IReadOnlyList<ExchangeStockLevel> OfferedItemStock)
{
    public CurrencyPairKey Pair => new(OfferedCurrency.Metadata, WantedCurrency.Metadata);
    public ExchangeStockLevel? TopImmediateStock => WantedItemStock.FirstOrDefault();
    public RationalExchangeRate? TopImmediateRate => TopImmediateStock?.SelectedPairRate;
    public ExchangeStockLevel? TopCompetingStock => OfferedItemStock.FirstOrDefault();
    public RationalExchangeRate? TopCompetingRate => TopCompetingStock?.SelectedPairRate;
}

public sealed class ExchangeRateBook
{
    private readonly Dictionary<CurrencyPairKey, ExchangePairSnapshot> _latestByPair = [];

    public IReadOnlyCollection<ExchangePairSnapshot> LatestSnapshots => _latestByPair.Values;

    public void Store(ExchangePairSnapshot snapshot)
    {
        _latestByPair[snapshot.Pair] = snapshot;
    }

    public bool TryGetLatest(CurrencyPairKey pair, out ExchangePairSnapshot? snapshot)
    {
        return _latestByPair.TryGetValue(pair, out snapshot);
    }

    public void Clear()
    {
        _latestByPair.Clear();
    }
}
