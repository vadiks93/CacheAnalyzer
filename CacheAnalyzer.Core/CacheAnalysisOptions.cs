namespace CacheAnalyzer.Core;

public sealed class CacheAnalysisOptions
{
    public bool Enabled { get; set; } = true;

    public EndpointSelectionMode EndpointSelection { get; set; } = EndpointSelectionMode.AttributedEndpoints;

    public long MaximumPayloadBytes { get; set; } = 1024 * 1024 * 3;

    public TimeSpan MaximumFingerprintDuration { get; set; } = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Gets or sets the maximum number of distinct request keys retained in memory.
    /// Defaults to 200. Once full, the tracker continues observing existing keys but
    /// rejects new keys. This does not limit incoming requests or observations per key.
    /// Keys are not automatically evicted; lowering this limit does not remove existing keys.
    /// Admission is best effort: concurrent new keys can exceed the configured limit.
    /// </summary>
    public int MaximumDistinctRequestKeys { get; set; } = 200;

    /// <summary>
    /// Gets or sets the maximum number of observations retained for each request key.
    /// Defaults to 1,000 and must be positive. Each observation prunes expired queue-head
    /// entries and oldest excess entries. Concurrent updates can temporarily exceed the
    /// limit or prune extra entries; snapshots exclude expired entries and are capped.
    /// Stability is evaluated over this retained history, not necessarily the whole window.
    /// Set this at least as high as MinimumObservations to allow cache recommendations.
    /// Changes apply to each key the next time it is observed.
    /// </summary>
    public int MaximumObservationsPerKey { get; set; } = 1_000;

    public int MinimumObservations { get; set; } = 10;

    public TimeSpan ObservationWindow { get; set; } = TimeSpan.FromHours(4);

    public double MinimumPayloadToFingerprintRatio { get; set; } = 10;
}
