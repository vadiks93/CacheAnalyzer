namespace CacheAnalyzer.Core;

public sealed class ResponseAnalysis
{
    public ResponseAnalysis(
        string requestKey,
        CacheRecommendation recommendation,
        long payloadBytes,
        TimeSpan fingerprintDuration,
        string? fingerprint,
        bool trackingCapacityReached = false)
    {
        RequestKey = requestKey;
        Recommendation = recommendation;
        PayloadBytes = payloadBytes;
        FingerprintDuration = fingerprintDuration;
        Fingerprint = fingerprint;
        TrackingCapacityReached = trackingCapacityReached;
    }

    public string RequestKey { get; }

    public CacheRecommendation Recommendation { get; }

    public long PayloadBytes { get; }

    public TimeSpan FingerprintDuration { get; }

    public string? Fingerprint { get; }

    public bool TrackingCapacityReached { get; }
}
