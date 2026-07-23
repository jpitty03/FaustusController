using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FaustusController;

public sealed class RateCaptureExportFile
{
    public int SchemaVersion { get; set; } = 4;
    public DateTimeOffset ExportedAtUtc { get; set; }
    public List<LatestRateCapture> Captures { get; set; } = [];
    public LatestBoundedScanManifest? LatestBoundedScan { get; set; }
    public LatestBoundedScanManifest? LatestCompletedBoundedScan { get; set; }
}

public sealed class LatestBoundedScanManifest
{
    public Guid ScanId { get; set; }
    public Guid CollectorSessionId { get; set; }
    public string League { get; set; } = "";
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? TerminalAtUtc { get; set; }
    public string State { get; set; } = "";
    public string? FailureReason { get; set; }
    public List<ScanPairReferenceCapture> PlannedPairs { get; set; } = [];
    public List<ScanCaptureReferenceCapture> PersistedCaptures { get; set; } = [];
}

public class ScanPairReferenceCapture
{
    public int Sequence { get; set; }
    public string OfferedMetadata { get; set; } = "";
    public string WantedMetadata { get; set; } = "";
}

public sealed class ScanCaptureReferenceCapture : ScanPairReferenceCapture
{
    public Guid CaptureId { get; set; }
}

public class RateCaptureObservation
{
    public Guid CaptureId { get; set; }
    public DateTimeOffset CapturedAtUtc { get; set; }
    public Guid CollectorSessionId { get; set; }
    public Guid? ScanId { get; set; }
    public int? ScanSequence { get; set; }
    public string Source { get; set; } = "";
    public int AreaInstanceId { get; set; }
    public RationalRateCapture? MarketRate { get; set; }
    public RationalRateCapture? TopImmediateRate { get; set; }
    public RationalRateCapture? TopCompetingRate { get; set; }
    public List<StockLevelCapture> WantedItemStock { get; set; } = [];
    public List<StockLevelCapture> OfferedItemStock { get; set; } = [];
}

public sealed class LatestRateCapture : RateCaptureObservation
{
    public string League { get; set; } = "";
    public CurrencyCapture OfferedCurrency { get; set; } = new();
    public CurrencyCapture WantedCurrency { get; set; } = new();
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
