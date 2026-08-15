namespace CacheAnalyzer.Core;

public enum CacheRecommendation
{
    FirstObservation,
    CacheCandidate,
    ResponseChanged,
    PayloadTooLarge,
    FingerprintTooSlow,
    InsufficientTraffic
}
