using ExileCore;
using Newtonsoft.Json;
using System.Numerics;

namespace FaustusController;

public sealed record NormalizedPickerPoint(float X, float Y);

public sealed class PickerButtonCalibrationData
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public NormalizedPickerPoint? WantedButton { get; set; }
    public NormalizedPickerPoint? OfferedButton { get; set; }
    // Optional (nullable) so schema version 1 files without it still load.
    public NormalizedPickerPoint? PlaceOrderButton { get; set; }
}

public sealed class PickerButtonCalibrationController
{
    private bool _wasPickerVisible;

    public PickerButtonCalibrationData Data { get; private set; } = new();
    public bool IsArmed { get; private set; }
    public bool IsDirty { get; private set; }
    public bool IsComplete => Data.WantedButton != null && Data.OfferedButton != null;
    public string Status { get; private set; } = "Press F12 to calibrate picker buttons.";

    public void Load(PickerButtonCalibrationData data)
    {
        Data = data;
        IsArmed = false;
        IsDirty = false;
        Status = IsComplete
            ? "Loaded wanted and offered picker-button calibration."
            : "Loaded partial picker-button calibration; press F12 to restart.";
    }

    public void Start(GameController gameController)
    {
        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        // Recalibrating the picker buttons must not discard the separately
        // captured Place Order point (F9).
        Data = new PickerButtonCalibrationData { PlaceOrderButton = Data.PlaceOrderButton };
        IsArmed = true;
        IsDirty = false;
        _wasPickerVisible = panel.IsVisible && panel.CurrencyPicker.IsVisible;
        Status = _wasPickerVisible
            ? "Calibration armed: close the picker, then manually open I want."
            : "Calibration armed: manually open I want, close it, then open I have.";
    }

    public void Tick(GameController gameController)
    {
        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (!panel.IsVisible)
        {
            _wasPickerVisible = false;
            return;
        }

        var pickerVisible = panel.CurrencyPicker.IsVisible;
        if (IsArmed && pickerVisible && !_wasPickerVisible)
        {
            CaptureCurrentButton(gameController);
        }

        _wasPickerVisible = pickerVisible;
    }

    public bool TryResolve(
        SharpDX.RectangleF panelRectangle,
        bool wantedButton,
        out Vector2 position)
    {
        return TryResolvePoint(
            wantedButton ? Data.WantedButton : Data.OfferedButton,
            panelRectangle,
            out position);
    }

    public bool TryResolvePlaceOrder(
        SharpDX.RectangleF panelRectangle,
        out Vector2 position)
    {
        return TryResolvePoint(Data.PlaceOrderButton, panelRectangle, out position);
    }

    private static bool TryResolvePoint(
        NormalizedPickerPoint? point,
        SharpDX.RectangleF panelRectangle,
        out Vector2 position)
    {
        if (point == null || panelRectangle.Width <= 0 || panelRectangle.Height <= 0)
        {
            position = default;
            return false;
        }

        position = new Vector2(
            panelRectangle.X + point.X * panelRectangle.Width,
            panelRectangle.Y + point.Y * panelRectangle.Height);
        return true;
    }

    public void CapturePlaceOrderButton(GameController gameController)
    {
        if (!gameController.Window.IsForeground())
        {
            Status = "Place-order calibration ignored: Path of Exile is not foreground.";
            return;
        }

        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (!panel.IsVisible || panel.CurrencyPicker.IsVisible)
        {
            Status = "Place-order calibration ignored: the exchange panel must be " +
                "visible with no open picker.";
            return;
        }

        var rectangle = panel.GetClientRectCache;
        var cursor = Input.MousePositionNum;
        if (rectangle.Width <= 0 || rectangle.Height <= 0 ||
            cursor.X < rectangle.X || cursor.X > rectangle.X + rectangle.Width ||
            cursor.Y < rectangle.Y || cursor.Y > rectangle.Y + rectangle.Height)
        {
            Status = "Place-order calibration ignored: cursor is outside the panel rectangle.";
            return;
        }

        Data.PlaceOrderButton = new NormalizedPickerPoint(
            (cursor.X - rectangle.X) / rectangle.Width,
            (cursor.Y - rectangle.Y) / rectangle.Height);
        Data.UpdatedAtUtc = DateTimeOffset.UtcNow;
        IsDirty = true;
        Status = "Captured Place Order button; order placement can now click it.";
    }

    public void MarkSaved()
    {
        IsDirty = false;
    }

    public void Cancel(string reason)
    {
        IsArmed = false;
        Status = reason;
    }

    private void CaptureCurrentButton(GameController gameController)
    {
        if (!gameController.Window.IsForeground())
        {
            Status = "Calibration ignored picker open: Path of Exile is not foreground.";
            return;
        }

        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        var rectangle = panel.GetClientRectCache;
        var cursor = Input.MousePositionNum;
        if (rectangle.Width <= 0 || rectangle.Height <= 0 ||
            cursor.X < rectangle.X || cursor.X > rectangle.X + rectangle.Width ||
            cursor.Y < rectangle.Y || cursor.Y > rectangle.Y + rectangle.Height)
        {
            Status = "Calibration ignored picker open: cursor is outside the panel rectangle.";
            return;
        }

        var normalized = new NormalizedPickerPoint(
            (cursor.X - rectangle.X) / rectangle.Width,
            (cursor.Y - rectangle.Y) / rectangle.Height);
        if (panel.CurrencyPicker.IsPickingWantedCurrency)
        {
            Data.WantedButton = normalized;
            Status = Data.OfferedButton == null
                ? "Captured I want button; close the picker and manually open I have."
                : "Captured I want button; calibration complete.";
        }
        else
        {
            Data.OfferedButton = normalized;
            Status = Data.WantedButton == null
                ? "Captured I have button; close the picker and manually open I want."
                : "Captured I have button; calibration complete.";
        }

        Data.UpdatedAtUtc = DateTimeOffset.UtcNow;
        IsDirty = true;
        if (IsComplete)
        {
            IsArmed = false;
        }
    }
}

public sealed class PickerButtonCalibrationStore
{
    private const int CurrentSchemaVersion = 1;

    public PickerButtonCalibrationData? Load(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var data = JsonConvert.DeserializeObject<PickerButtonCalibrationData>(
            File.ReadAllText(path));
        if (data == null || data.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException("Picker-button calibration is invalid or unsupported.");
        }

        return data;
    }

    public void Save(PickerButtonCalibrationData data, string path)
    {
        data.SchemaVersion = CurrentSchemaVersion;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = path + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonConvert.SerializeObject(data, Formatting.Indented));
        File.Move(temporaryPath, path, overwrite: true);
    }
}
