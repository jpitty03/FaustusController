using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using Vector2 = System.Numerics.Vector2;

namespace FaustusController;

public sealed class FaustusController : BaseSettingsPlugin<FaustusControllerSettings>
{
    private readonly CurrencyExchangeRateCollector _collector = new();
    private readonly ExchangeRateBook _rateBook = new();
    private readonly RateCaptureJsonExporter _exporter = new();
    private string _captureStatus = "Use the capture hotkey with an exchange pair selected.";
    private string _exportPath = "";
    private string _exportStatus = "";

    public override bool Initialise()
    {
        _exportPath = Path.Combine(ConfigDirectory, "FaustusController_rate-captures.json");
        if (File.Exists(_exportPath))
        {
            try
            {
                var exportedCount = _exporter.Export([], _exportPath);
                _exportStatus = $"Validated {exportedCount} exported pairs at {_exportPath}";
            }
            catch (Exception exception)
            {
                _exportStatus = $"Rate export validation failed: {exception.Message}";
            }
        }
        else
        {
            _exportStatus = $"Capture export: {_exportPath}";
        }

        return true;
    }

    public override void AreaChange(AreaInstance area)
    {
        _captureStatus = "Area changed; captured rate snapshots were retained.";
    }

    public override Job Tick()
    {
        if (Settings.CaptureCurrentRate.PressedOnce())
        {
            if (_collector.TryCaptureCurrentPair(GameController, out var snapshot, out var failureReason))
            {
                _rateBook.Store(snapshot!);
                var immediateStock = snapshot!.TopImmediateStock;
                var rawImmediate = immediateStock == null
                    ? ""
                    : $" (raw wanted {immediateStock.RawGet}:{immediateStock.RawGive})";
                var competingStock = snapshot.TopCompetingStock;
                var rawCompeting = competingStock == null
                    ? ""
                    : $" (raw opposite {competingStock.RawGet}:{competingStock.RawGive})";
                _captureStatus = $"Captured {snapshot!.OfferedCurrency.Name} -> " +
                    $"{snapshot.WantedCurrency.Name}. Market: {FormatRatio(snapshot.MarketRate)}; " +
                    $"immediate: {FormatRatio(snapshot.TopImmediateRate)}{rawImmediate}; " +
                    $"competing: {FormatRatio(snapshot.TopCompetingRate)}{rawCompeting}.";

                try
                {
                    var exportedCount = _exporter.Export(
                        _rateBook.LatestSnapshots,
                        _exportPath);
                    _exportStatus = $"Exported {exportedCount} pairs to {_exportPath}";
                }
                catch (Exception exception)
                {
                    _exportStatus = $"Rate export failed: {exception.Message}";
                }
            }
            else
            {
                _captureStatus = failureReason;
            }
        }

        return null!;
    }

    private static string FormatRatio(RationalExchangeRate? rate)
    {
        return rate == null ? "unavailable" : $"{rate.GetUnits}:{rate.GiveUnits}";
    }

    public override void Render()
    {
        Graphics.DrawText(_captureStatus, new Vector2(100, 100), SharpDX.Color.White);
        Graphics.DrawText(
            $"Captured pairs: {_rateBook.LatestSnapshots.Count}",
            new Vector2(100, 120),
            SharpDX.Color.White);
        Graphics.DrawText(_exportStatus, new Vector2(100, 140), SharpDX.Color.White);
    }

}
