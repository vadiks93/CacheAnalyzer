using System.Collections.Concurrent;

namespace CacheAnalyzer.Core;

/// <summary>Historical findings for one analysis run; snapshots are point-in-time copies.</summary>
public sealed class CacheCandidateStore
{
    private readonly ConcurrentDictionary<string, ResponseAnalysis> _candidates = new(StringComparer.Ordinal);

    public int Count => _candidates.Count;
    public bool Contains(string requestKey) => _candidates.ContainsKey(requestKey);
    public ResponseAnalysis[] GetSnapshot() => _candidates.Values.ToArray();

    // Admission is best effort under concurrency; callers stop analysis at capacity.
    public bool TryAdd(ResponseAnalysis analysis) => _candidates.TryAdd(analysis.RequestKey, analysis);
}
