using CacheAnalyzer.Core.Models;
using System.Collections.Concurrent;

namespace CacheAnalyzer.Core.Services.Candidates;

/// <summary>Historical findings for one analysis run; snapshots are point-in-time copies.</summary>
public sealed class CacheCandidateStore
{
    private readonly ConcurrentDictionary<string, ResponseAnalysis> _cacheableCandidates = new(StringComparer.Ordinal);

    public int Count => _cacheableCandidates.Count;
    public bool Contains(string requestKey) => _cacheableCandidates.ContainsKey(requestKey);
    public ResponseAnalysis[] GetSnapshot() => _cacheableCandidates.Values.ToArray();

    // Admission is best effort under concurrency; callers stop analysis at capacity.
    public bool TryAdd(ResponseAnalysis analysis) => _cacheableCandidates.TryAdd(analysis.RequestKey, analysis);
}
