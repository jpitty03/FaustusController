using Newtonsoft.Json;

namespace FaustusController;

public sealed class RateCaptureJsonExporter
{
    private const int CurrentSchemaVersion = 1;

    public int Export(
        IReadOnlyCollection<ExchangePairSnapshot> snapshots,
        string outputPath)
    {
        var captures = LoadExisting(outputPath)
            .ToDictionary(CaptureKey, StringComparer.Ordinal);

        foreach (var snapshot in snapshots)
        {
            var capture = CreateCapture(snapshot);
            captures[CaptureKey(capture)] = capture;
        }

        var export = new RateCaptureExportFile
        {
            SchemaVersion = CurrentSchemaVersion,
            ExportedAtUtc = DateTimeOffset.UtcNow,
            Captures = captures.Values
                .OrderBy(capture => capture.OfferedCurrency.Metadata, StringComparer.Ordinal)
                .ThenBy(capture => capture.WantedCurrency.Metadata, StringComparer.Ordinal)
                .ToList()
        };

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = outputPath + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonConvert.SerializeObject(export, Formatting.Indented));
        File.Move(temporaryPath, outputPath, overwrite: true);
        return export.Captures.Count;
    }

    private static IReadOnlyCollection<RateCapture> LoadExisting(string outputPath)
    {
        if (!File.Exists(outputPath))
        {
            return [];
        }

        var export = JsonConvert.DeserializeObject<RateCaptureExportFile>(
            File.ReadAllText(outputPath));
        if (export == null)
        {
            throw new InvalidDataException("The existing rate capture export is empty or invalid.");
        }

        if (export.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported rate capture schema version {export.SchemaVersion}.");
        }

        return export.Captures;
    }

    private static RateCapture CreateCapture(ExchangePairSnapshot snapshot)
    {
        return new RateCapture
        {
            CapturedAtUtc = snapshot.CapturedAtUtc,
            OfferedCurrency = CreateCurrency(snapshot.OfferedCurrency),
            WantedCurrency = CreateCurrency(snapshot.WantedCurrency),
            MarketRate = CreateRate(snapshot.MarketRate),
            TopImmediateRate = CreateRate(snapshot.TopImmediateRate),
            TopCompetingRate = CreateRate(snapshot.TopCompetingRate),
            WantedItemStock = snapshot.WantedItemStock.Select(CreateStock).ToList(),
            OfferedItemStock = snapshot.OfferedItemStock.Select(CreateStock).ToList()
        };
    }

    private static CurrencyCapture CreateCurrency(CurrencyIdentity currency)
    {
        return new CurrencyCapture
        {
            Metadata = currency.Metadata,
            Hash = currency.Hash,
            Name = currency.Name
        };
    }

    private static StockLevelCapture CreateStock(ExchangeStockLevel stock)
    {
        return new StockLevelCapture
        {
            Side = stock.Side.ToString(),
            RawGet = stock.RawGet,
            RawGive = stock.RawGive,
            ListedCount = stock.ListedCount,
            RawRate = CreateRate(stock.RawRate)!,
            SelectedPairRate = CreateRate(stock.SelectedPairRate)!
        };
    }

    private static RationalRateCapture? CreateRate(RationalExchangeRate? rate)
    {
        return rate == null
            ? null
            : new RationalRateCapture
            {
                RawGet = rate.RawGet,
                RawGive = rate.RawGive,
                GetUnits = rate.GetUnits,
                GiveUnits = rate.GiveUnits,
                WantedPerOffered = rate.WantedPerOffered
            };
    }

    private static string CaptureKey(RateCapture capture)
    {
        return capture.OfferedCurrency.Metadata + "\0" + capture.WantedCurrency.Metadata;
    }
}

public sealed class RateCaptureExportFile
{
    public int SchemaVersion { get; set; }
    public DateTimeOffset ExportedAtUtc { get; set; }
    public List<RateCapture> Captures { get; set; } = [];
}

public sealed class RateCapture
{
    public DateTimeOffset CapturedAtUtc { get; set; }
    public CurrencyCapture OfferedCurrency { get; set; } = new();
    public CurrencyCapture WantedCurrency { get; set; } = new();
    public RationalRateCapture? MarketRate { get; set; }
    public RationalRateCapture? TopImmediateRate { get; set; }
    public RationalRateCapture? TopCompetingRate { get; set; }
    public List<StockLevelCapture> WantedItemStock { get; set; } = [];
    public List<StockLevelCapture> OfferedItemStock { get; set; } = [];
}

public sealed class CurrencyCapture
{
    public string Metadata { get; set; } = "";
    public uint Hash { get; set; }
    public string Name { get; set; } = "";
}

public sealed class RationalRateCapture
{
    public int RawGet { get; set; }
    public int RawGive { get; set; }
    public int GetUnits { get; set; }
    public int GiveUnits { get; set; }
    public decimal WantedPerOffered { get; set; }
}

public sealed class StockLevelCapture
{
    public string Side { get; set; } = "";
    public int RawGet { get; set; }
    public int RawGive { get; set; }
    public int ListedCount { get; set; }
    public RationalRateCapture RawRate { get; set; } = new();
    public RationalRateCapture SelectedPairRate { get; set; } = new();
}
