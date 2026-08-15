namespace CacheAnalyzer.Core;

public interface IResponseFingerprintHistory
{
    FingerprintHistoryResult Observe(
        string requestKey,
        string fingerprint,
        long payloadBytes,
        DateTimeOffset observedAt,
        CacheAnalysisOptions options);
}
