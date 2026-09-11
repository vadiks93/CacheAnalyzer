using System.Collections.Concurrent;

namespace CacheAnalyzer.Core;

public sealed class InMemoryResponseFingerprintHistory : IResponseFingerprintHistory
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Observation>> _observations =
        new(StringComparer.Ordinal);

    public FingerprintHistoryResult Observe(
        string requestKey,
        string fingerprint,
        long payloadBytes,
        DateTimeOffset observedAt,
        CacheAnalysisOptions options)
    {
        if (options.MaximumObservationsPerKey <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaximumObservationsPerKey),
                "The observation limit must be positive.");
        }

        if (!_observations.TryGetValue(requestKey, out var observations))
        {
            // Admission is deliberately approximate when new keys arrive concurrently.
            if (_observations.Count >= options.MaximumDistinctRequestKeys)
            {
                return new FingerprintHistoryResult(
                    CacheRecommendation.InsufficientTraffic,
                    trackingCapacityReached: true);
            }

            observations = _observations.GetOrAdd(requestKey, static _ => new ConcurrentQueue<Observation>());
        }

        observations.Enqueue(new Observation(fingerprint, observedAt));
        var windowStart = observedAt - options.ObservationWindow;
        while (observations.TryPeek(out var oldest) &&
               (oldest.ObservedAt < windowStart || observations.Count > options.MaximumObservationsPerKey))
        {
            observations.TryDequeue(out _);
        }

        // Other requests may enqueue or prune during this operation. Approximate history
        // is intentional: never add application locks to make recommendations consistent.
        var retained = observations.ToArray();
        var snapshot = retained
            .Skip(Math.Max(0, retained.Length - options.MaximumObservationsPerKey))
            .Where(observation => observation.ObservedAt >= windowStart)
            .ToArray();
        if (snapshot.Length == 0)
        {
            return new FingerprintHistoryResult(CacheRecommendation.InsufficientTraffic);
        }
        if (snapshot.Length == 1)
        {
            return new FingerprintHistoryResult(CacheRecommendation.FirstObservation);
        }

        if (snapshot.Any(observation =>
            !string.Equals(observation.Fingerprint, fingerprint, StringComparison.Ordinal)))
        {
            return new FingerprintHistoryResult(CacheRecommendation.ResponseChanged);
        }

        var fingerprintBytes = fingerprint.Length;
        var hasUsefulRatio = fingerprintBytes > 0 &&
                             payloadBytes / (double)fingerprintBytes >= options.MinimumPayloadToFingerprintRatio;
        var hasEnoughTraffic = snapshot.Length >= options.MinimumObservations;

        return new FingerprintHistoryResult(
            hasUsefulRatio && hasEnoughTraffic
                ? CacheRecommendation.CacheCandidate
                : CacheRecommendation.InsufficientTraffic);
    }

    private readonly struct Observation
    {
        public Observation(string fingerprint, DateTimeOffset observedAt)
        {
            Fingerprint = fingerprint;
            ObservedAt = observedAt;
        }

        public string Fingerprint { get; }

        public DateTimeOffset ObservedAt { get; }
    }
}
