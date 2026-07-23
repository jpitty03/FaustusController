using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FaustusController;

internal sealed class LegacyRateHistoryExportFile
{
    public List<LegacyRateCaptureSeries> Series { get; set; } = [];
}

internal sealed class LegacyLatestRateCaptureExportFile
{
    public List<LatestRateCapture> Captures { get; set; } = [];
}

internal sealed class LegacyRateCaptureSeries
{
    public string League { get; set; } = "";
    public CurrencyCapture OfferedCurrency { get; set; } = new();
    public CurrencyCapture WantedCurrency { get; set; } = new();
    public List<RateCaptureObservation> History { get; set; } = [];
}

internal sealed class LegacyRateCaptureExportFile
{
    public List<LegacySchema1Capture> Captures { get; set; } = [];
}

internal sealed class LegacySchema1Capture
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
