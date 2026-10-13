namespace CacheAnalyzer.Core.Models;

public enum CacheRecommendation
{
    FirstObservation,
    CacheCandidate,
    ResponseChanged,
    PayloadTooLarge,
    FingerprintTooSlow,
    InsufficientTraffic
}
