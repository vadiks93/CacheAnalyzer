namespace CacheAnalyzer.Core;

public sealed class CacheAnalysisOptions
{
    public bool Enabled { get; set; } = true;

    public EndpointSelectionMode EndpointSelection { get; set; } = EndpointSelectionMode.AttributedEndpoints;

    public long MaximumPayloadBytes { get; set; } = 1024 * 1024 * 3;

    public TimeSpan MaximumFingerprintDuration { get; set; } = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Gets or sets the maximum number of distinct request keys retained in memory.
    /// Defaults to 350. The middleware checks this limit outside history and permanently
    /// bypasses analysis once reached, logs once, and clears retained history.
    /// This does not limit incoming requests. Direct history callers enforce their own policy.
    /// Capacity checks are best effort: concurrent observations can exceed the limit;
    /// in-flight requests release remaining history when they finish.
    /// </summary>
    public int MaximumDistinctRequestKeys { get; set; } = 350;

    /// <summary>
    /// Gets or sets the maximum number of observations retained for each request key.
    /// Defaults to 1,000 and must be positive. Each observation prunes expired queue-head
    /// entries and oldest excess entries. Concurrent updates can temporarily exceed the
    /// limit or prune extra entries; snapshots exclude expired entries and are capped.
    /// Stability is evaluated over this retained history, not necessarily the whole window.
    /// Set this at least as high as MinimumObservationsPerKey to allow cache recommendations.
    /// Changes apply to each key the next time it is observed.
    /// </summary>
    public int MaximumObservationsPerKey { get; set; } = 1_000;

    /// <summary>
    /// Gets or sets the minimum retained observation count for a single request key.
    /// Defaults to 10. Only observations within ObservationWindow count, subject to
    /// MaximumObservationsPerKey. A candidate also requires matching fingerprints and
    /// a sufficient payload-to-fingerprint ratio; counts are not pooled across keys.
    /// </summary>
    public int MinimumObservationsPerKey { get; set; } = 10;

    public TimeSpan ObservationWindow { get; set; } = TimeSpan.FromHours(4);

    public double MinimumPayloadToFingerprintRatio { get; set; } = 10;
}
