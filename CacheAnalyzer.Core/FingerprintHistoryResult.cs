namespace CacheAnalyzer.Core;

public readonly struct FingerprintHistoryResult
{
    public FingerprintHistoryResult(
        CacheRecommendation recommendation,
        bool trackingCapacityReached = false)
    {
        Recommendation = recommendation;
        TrackingCapacityReached = trackingCapacityReached;
    }

    public CacheRecommendation Recommendation { get; }

    public bool TrackingCapacityReached { get; }
}
