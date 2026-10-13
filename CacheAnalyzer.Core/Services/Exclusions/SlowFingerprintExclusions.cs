using CacheAnalyzer.Core.Models;
using CacheAnalyzer.Core.Options;
using System.Collections.Concurrent;
using System.Threading;

namespace CacheAnalyzer.Core.Services.Exclusions;

/// <summary>Best-effort, bounded exclusions for expensive fingerprint analysis.</summary>
public sealed class SlowFingerprintExclusions
{
    private State _state = new();

    public bool IsExcluded(string requestKey, string? getPathKey, CacheAnalysisOptions options)
    {
        var state = Volatile.Read(ref _state);
        return state.Groups.TryGetValue(getPathKey ?? requestKey, out var group) &&
            (Volatile.Read(ref group.Excluded) != 0 || group.Requests.ContainsKey(requestKey));
    }

    /// <summary>
    /// Records a distinct slow request, reporting path exclusion or overall capacity overflow.
    /// Previously excluded requests do not count again. Concurrent admission can overshoot.
    /// </summary>
    public SlowFingerprintRecordResult RecordSlowRequest(string requestKey, string? getPathKey, CacheAnalysisOptions options)
    {
        var state = Volatile.Read(ref _state);
        var groupKey = getPathKey ?? requestKey;
        if (!state.Groups.TryGetValue(groupKey, out var group))
        {
            if (state.Groups.Count >= options.MaximumSlowFingerprintRequests)
                return SlowFingerprintRecordResult.CapacityExceeded;
            group = state.Groups.GetOrAdd(groupKey, static _ => new Group());
        }
        if (state.Groups.Count > options.MaximumSlowFingerprintRequests)
            return SlowFingerprintRecordResult.CapacityExceeded;
        if (Volatile.Read(ref group.Excluded) != 0) return SlowFingerprintRecordResult.Recorded;
        group.Requests.TryAdd(requestKey, 0);
        var newlyExcluded = getPathKey is not null &&
            group.Requests.Count > options.MaximumSlowFingerprintRequestsPerPath &&
            Interlocked.Exchange(ref group.Excluded, 1) == 0;
        if (Volatile.Read(ref group.Excluded) != 0) group.Requests.Clear();
        return newlyExcluded ? SlowFingerprintRecordResult.PathExcluded : SlowFingerprintRecordResult.Recorded;
    }

    public void Clear() => Interlocked.Exchange(ref _state, new State());

    private sealed class State
    {
        public ConcurrentDictionary<string, Group> Groups { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Group
    {
        public ConcurrentDictionary<string, byte> Requests { get; } = new(StringComparer.Ordinal);
        public int Excluded;
    }
}
