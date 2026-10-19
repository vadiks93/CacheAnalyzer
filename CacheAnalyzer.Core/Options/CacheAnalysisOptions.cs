using CacheAnalyzer.Core.Models;
namespace CacheAnalyzer.Core.Options;

public sealed class CacheAnalysisOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Opt in to including response fingerprints in the default analysis logs.</summary>
    public bool IncludeFingerprintInLogs { get; set; } = false;

    /// <summary>Opt in to retaining and logging the full request key, including query values.</summary>
    public bool IncludeFullRequestKeyInLogs { get; set; } = false;

    /// <summary>
    /// Stop analysis after exceeding this number of distinct cache candidates (default 200).
    /// Findings are preserved; capacity accounting is best effort under concurrency.
    /// </summary>
    public int MaximumCacheCandidates { get; set; } = 200;

    public EndpointSelectionMode EndpointSelection { get; set; } = EndpointSelectionMode.AttributedEndpoints;

    /// <summary>
    /// Maximum response size eligible for hashing. The ASP.NET Core adapter also
    /// caps capture capacity at this limit, forwarding oversized responses without
    /// analysis. Eligible responses allocate a separate copy for hashing; this is
    /// not a total memory limit or a limit on QUERY request-body buffering.
    /// </summary>
    public long MaximumPayloadBytes { get; set; } = 1024 * 1024 * 3;

    public TimeSpan MaximumFingerprintDuration { get; set; } = TimeSpan.FromMilliseconds(80);

    /// <summary>
    /// Maximum distinct slow GET query variants per origin/path, default 500.
    /// Another distinct slow variant excludes that path, without stopping other paths.
    /// </summary>
    public int MaximumSlowFingerprintRequestsPerPath { get; set; } = 500;

    /// <summary>
    /// Maximum retained slow request groups, default 500: each GET origin/path counts
    /// once, and each exact QUERY body/metadata key counts once. Another group stops
    /// all analysis and clears retained state. Concurrent admission is best effort.
    /// </summary>
    public int MaximumSlowFingerprintRequests { get; set; } = 500;

    /// <summary>
    /// Gets or sets the maximum number of distinct requests retained in memory.
    /// Defaults to 350. The middleware checks this limit outside history and permanently
    /// bypasses analysis once reached, logs once, and clears retained history.
    /// This does not limit incoming requests. Direct history callers enforce their own policy.
    /// Capacity checks are best effort: concurrent observations can exceed the limit;
    /// in-flight requests release remaining history when they finish.
    /// </summary>
    public int MaximumDistinctRequests { get; set; } = 350;

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
