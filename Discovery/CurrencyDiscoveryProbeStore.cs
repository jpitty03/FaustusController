using Newtonsoft.Json;

namespace FaustusController;

public enum CurrencyDiscoveryProbeStatus
{
    Active,
    NoMarketRate,
    Unavailable,
    Failed
}

public sealed record CurrencyDiscoveryProbeOutcome(
    string League,
    CurrencyIdentity OfferedCurrency,
    CurrencyIdentity WantedCurrency,
    CurrencyDiscoveryProbeStatus Status,
    DateTimeOffset ObservedAtUtc,
    Guid RunId,
    int RunSequence,
    Guid? CaptureId,
    string? FailureReason)
{
    public CurrencyPairKey Pair => new(
        OfferedCurrency.Metadata,
        WantedCurrency.Metadata);
}

public sealed class CurrencyDiscoveryProbeStore
{
    private const int CurrentSchemaVersion = 1;

    public IReadOnlyCollection<CurrencyDiscoveryProbeOutcome> Load(
        string inputPath,
        string league)
    {
        if (!File.Exists(inputPath))
        {
            return [];
        }

        var file = JsonConvert.DeserializeObject<CurrencyDiscoveryProbeFile>(
            File.ReadAllText(inputPath)) ??
            throw new InvalidDataException("The discovery-probe file is empty.");
        if (file.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported discovery-probe schema version {file.SchemaVersion}.");
        }

        if (!string.Equals(file.League, league, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Discovery-probe file league '{file.League}' does not match expected league '{league}'.");
        }

        var byPair = new Dictionary<CurrencyPairKey, CurrencyDiscoveryProbeOutcome>();
        foreach (var capture in file.Outcomes)
        {
            var outcome = Restore(capture, league);
            if (byPair.ContainsKey(outcome.Pair))
            {
                throw new InvalidDataException(
                    "The discovery-probe file contains a duplicate directed pair.");
            }

            byPair[outcome.Pair] = outcome;
        }

        return byPair.Values.ToArray();
    }

    public void Save(
        IReadOnlyCollection<CurrencyDiscoveryProbeOutcome> outcomes,
        string league,
        int catalogueCurrencyCount,
        long eligibleProbePairCount,
        string outputPath)
    {
        var byPair = new Dictionary<CurrencyPairKey, CurrencyDiscoveryProbeOutcome>();
        foreach (var outcome in outcomes)
        {
            Validate(outcome, league);
            byPair[outcome.Pair] = outcome;
        }

        var file = new CurrencyDiscoveryProbeFile
        {
            SchemaVersion = CurrentSchemaVersion,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            League = league,
            CatalogueCurrencyCount = catalogueCurrencyCount,
            EligibleProbePairCount = eligibleProbePairCount,
            ActiveCount = byPair.Values.Count(
                outcome => outcome.Status == CurrencyDiscoveryProbeStatus.Active),
            NoMarketRateCount = byPair.Values.Count(
                outcome => outcome.Status == CurrencyDiscoveryProbeStatus.NoMarketRate),
            UnavailableCount = byPair.Values.Count(
                outcome => outcome.Status == CurrencyDiscoveryProbeStatus.Unavailable),
            FailedCount = byPair.Values.Count(
                outcome => outcome.Status == CurrencyDiscoveryProbeStatus.Failed),
            Outcomes = byPair.Values
                .OrderBy(outcome => outcome.OfferedCurrency.Name, StringComparer.Ordinal)
                .ThenBy(outcome => outcome.OfferedCurrency.Metadata, StringComparer.Ordinal)
                .ThenBy(outcome => outcome.WantedCurrency.Metadata, StringComparer.Ordinal)
                .Select(CreateCapture)
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
            JsonConvert.SerializeObject(file, Formatting.Indented));
        File.Move(temporaryPath, outputPath, overwrite: true);
    }

    private static CurrencyDiscoveryProbeOutcome Restore(
        CurrencyDiscoveryProbeCapture capture,
        string league)
    {
        if (!Enum.GetNames<CurrencyDiscoveryProbeStatus>().Contains(
                capture.Status,
                StringComparer.Ordinal) ||
            !Enum.TryParse<CurrencyDiscoveryProbeStatus>(capture.Status, out var status) ||
            !Enum.IsDefined(status))
        {
            throw new InvalidDataException(
                $"Unknown discovery-probe status '{capture.Status}'.");
        }

        var outcome = new CurrencyDiscoveryProbeOutcome(
            league,
            new CurrencyIdentity(
                capture.OfferedCurrency.Metadata,
                capture.OfferedCurrency.Hash,
                capture.OfferedCurrency.Name),
            new CurrencyIdentity(
                capture.WantedCurrency.Metadata,
                capture.WantedCurrency.Hash,
                capture.WantedCurrency.Name),
            status,
            capture.ObservedAtUtc,
            capture.RunId,
            capture.RunSequence,
            capture.CaptureId,
            capture.FailureReason);
        Validate(outcome, league);
        return outcome;
    }

    private static void Validate(
        CurrencyDiscoveryProbeOutcome outcome,
        string league)
    {
        if (!string.Equals(outcome.League, league, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(outcome.OfferedCurrency.Metadata) ||
            string.IsNullOrWhiteSpace(outcome.WantedCurrency.Metadata) ||
            outcome.Pair.OfferedMetadata == outcome.Pair.WantedMetadata ||
            outcome.ObservedAtUtc == default || outcome.RunId == Guid.Empty ||
            outcome.RunSequence <= 0)
        {
            throw new InvalidDataException("A discovery-probe outcome has invalid identity.");
        }

        if (outcome.Status == CurrencyDiscoveryProbeStatus.Active &&
            (!outcome.CaptureId.HasValue || outcome.CaptureId.Value == Guid.Empty))
        {
            throw new InvalidDataException("An active discovery probe requires a capture ID.");
        }

        if (outcome.Status != CurrencyDiscoveryProbeStatus.Active && outcome.CaptureId != null)
        {
            throw new InvalidDataException(
                "A non-active discovery probe cannot reference a rate capture.");
        }

        if ((outcome.Status == CurrencyDiscoveryProbeStatus.Failed) !=
            !string.IsNullOrWhiteSpace(outcome.FailureReason))
        {
            throw new InvalidDataException(
                "Discovery-probe failure metadata is inconsistent.");
        }
    }

    private static CurrencyDiscoveryProbeCapture CreateCapture(
        CurrencyDiscoveryProbeOutcome outcome)
    {
        return new CurrencyDiscoveryProbeCapture
        {
            OfferedCurrency = CreateCurrency(outcome.OfferedCurrency),
            WantedCurrency = CreateCurrency(outcome.WantedCurrency),
            Status = outcome.Status.ToString(),
            ObservedAtUtc = outcome.ObservedAtUtc,
            RunId = outcome.RunId,
            RunSequence = outcome.RunSequence,
            CaptureId = outcome.CaptureId,
            FailureReason = outcome.FailureReason
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
}

public sealed class CurrencyDiscoveryProbeFile
{
    public int SchemaVersion { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string League { get; set; } = "";
    public int CatalogueCurrencyCount { get; set; }
    public long EligibleProbePairCount { get; set; }
    public int ActiveCount { get; set; }
    public int NoMarketRateCount { get; set; }
    public int UnavailableCount { get; set; }
    public int FailedCount { get; set; }
    public List<CurrencyDiscoveryProbeCapture> Outcomes { get; set; } = [];
}

public sealed class CurrencyDiscoveryProbeCapture
{
    public CurrencyCapture OfferedCurrency { get; set; } = new();
    public CurrencyCapture WantedCurrency { get; set; } = new();
    public string Status { get; set; } = "";
    public DateTimeOffset ObservedAtUtc { get; set; }
    public Guid RunId { get; set; }
    public int RunSequence { get; set; }
    public Guid? CaptureId { get; set; }
    public string? FailureReason { get; set; }
}
