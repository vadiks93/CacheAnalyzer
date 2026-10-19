namespace CacheAnalyzer.Core.Models;

public enum CacheRecommendation
{
    FirstObservation,
    CacheCandidate,
    ResponseChanged,
    PayloadTooLarge,
    FingerprintTooSlow,
    InsufficientTraffic,
    /// <summary>Included JSON data was stable; excluded metadata may still change.
    /// Does not establish whole-response cacheability.</summary>
    StableDataCandidate
}
