namespace FaustusController;

public sealed class PlacedOrdersFile
{
    public int SchemaVersion { get; set; }
    public DateTimeOffset CapturedAtUtc { get; set; }
    public string League { get; set; } = "";
    public int PendingCount { get; set; }
    public int CompletedCount { get; set; }
    public int CanceledCount { get; set; }
    public long TotalGoldCost { get; set; }
    public long MedianGoldCostPerOrder { get; set; }
    public List<PlacedOrderCapture> Orders { get; set; } = [];
}

public sealed class PlacedOrderCapture
{
    public int PlayerOrderId { get; set; }
    public DateTimeOffset CreationDate { get; set; }
    public string Status { get; set; } = "";
    public CurrencyCapture OfferedCurrency { get; set; } = new();
    public CurrencyCapture WantedCurrency { get; set; } = new();
    public int OriginalOfferedStackSize { get; set; }
    public int RemainingOfferedStackSize { get; set; }
    public int WantedStackSize { get; set; }
    public int GoldCost { get; set; }
    public int OfferedRatioPart { get; set; }
    public int WantedRatioPart { get; set; }
    public int CompetingOfferedRatioPart { get; set; }
    public int CompetingWantedRatioPart { get; set; }
}
