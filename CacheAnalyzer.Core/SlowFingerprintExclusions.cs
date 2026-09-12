using System.Collections.Concurrent;
using System.Threading;

namespace CacheAnalyzer.Core;

/// <summary>Best-effort, bounded exclusions for expensive fingerprint analysis.</summary>
public sealed class SlowFingerprintExclusions
{
    private State _state = new();

    public bool IsExcluded(string requestKey, string? getPathKey, CacheAnalysisOptions options)
    {
        var state = Volatile.Read(ref _state);
        return state.Requests.ContainsKey(requestKey) ||
            (getPathKey is not null && state.Paths.TryGetValue(getPathKey, out var count) &&
             count >= options.SlowFingerprintVariantsPerPath);
    }

    /// <summary>
    /// Records a distinct slow request. Returns true when the caller should stop analysis.
    /// Previously excluded requests do not count again. Concurrent admission can overshoot.
    /// </summary>
    public bool Record(string requestKey, string? getPathKey, CacheAnalysisOptions options)
    {
        var state = Volatile.Read(ref _state);
        if (IsExcluded(requestKey, getPathKey, options)) return false;
        if (state.Requests.Count >= options.MaximumSlowFingerprintRequests) return true;
        if (state.Requests.TryAdd(requestKey, 0) && getPathKey is not null)
        {
            state.Paths.AddOrUpdate(getPathKey, 1, static (_, count) => count + 1);
        }

        return state.Requests.Count > options.MaximumSlowFingerprintRequests;
    }

    public void Clear() => Interlocked.Exchange(ref _state, new State());

    private sealed class State
    {
        public ConcurrentDictionary<string, byte> Requests { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, int> Paths { get; } = new(StringComparer.Ordinal);
    }
}
