namespace CacheAnalyzer.Core;

public sealed class CacheAnalysisOptions
{
    public bool Enabled { get; set; } = true;

    public EndpointSelectionMode EndpointSelection { get; set; } = EndpointSelectionMode.AttributedEndpoints;

    public long MaximumPayloadBytes { get; set; } = 1024 * 1024 * 3;

    public TimeSpan MaximumFingerprintDuration { get; set; } = TimeSpan.FromMilliseconds(25);

    public int MaximumTrackedRequests { get; set; } = 20;

    public int MinimumObservations { get; set; } = 10;

    public TimeSpan ObservationWindow { get; set; } = TimeSpan.FromHours(4);

    public double MinimumPayloadToFingerprintRatio { get; set; } = 10;
}
