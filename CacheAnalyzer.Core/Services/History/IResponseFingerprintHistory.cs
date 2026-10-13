using CacheAnalyzer.Core.Models;
using CacheAnalyzer.Core.Options;
namespace CacheAnalyzer.Core.Services.History;

public interface IResponseFingerprintHistory
{
    /// <summary>Returns the current distinct-key count; concurrent observations may change it immediately.</summary>
    int GetCountDistinctRequests();

    /// <summary>Releases retained history. Must support concurrent observations and cleanup calls.</summary>
    void Clear();

    FingerprintHistoryResult Observe(
        string requestKey,
        string fingerprint,
        long payloadBytes,
        DateTimeOffset observedAt,
        CacheAnalysisOptions options);
}
