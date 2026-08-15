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
        if (!_observations.TryGetValue(requestKey, out var observations))
        {
            if (_observations.Count >= options.MaximumTrackedRequests)
            {
                return new FingerprintHistoryResult(
                    CacheRecommendation.InsufficientTraffic,
                    trackingCapacityReached: true);
            }

            observations = _observations.GetOrAdd(
                requestKey,
                static _ => new ConcurrentQueue<Observation>());
        }

        observations.Enqueue(new Observation(fingerprint, observedAt));

        var windowStart = observedAt - options.ObservationWindow;
        while (observations.TryPeek(out var oldest) && oldest.ObservedAt < windowStart)
        {
            observations.TryDequeue(out _);
        }

        // Enumeration is a moment-in-time snapshot. Concurrent requests may make
        // this conservative, but no request waits for another request.
        var snapshot = observations.ToArray();
        if (snapshot.Length == 1)
        {
            return new FingerprintHistoryResult(CacheRecommendation.FirstObservation);
        }

        if (!string.Equals(snapshot[snapshot.Length - 2].Fingerprint, fingerprint, StringComparison.Ordinal))
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
