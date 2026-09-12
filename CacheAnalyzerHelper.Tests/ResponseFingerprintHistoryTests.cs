using CacheAnalyzer.Core;

namespace CacheAnalyzerHelper.Tests;

public sealed class ResponseFingerprintHistoryTests
{
    [Fact]
    public void Defaults_BoundKeysAndObservations()
    {
        var options = new CacheAnalysisOptions();
        Assert.Equal(350, options.MaximumDistinctRequestKeys);
        Assert.Equal(1_000, options.MaximumObservationsPerKey);
        Assert.Equal(10, options.MinimumObservationsPerKey);
    }

    [Fact]
    public void Stability_RequiresAllRetainedFingerprintsToMatch()
    {
        var history = new InMemoryResponseFingerprintHistory();
        var options = Options(limit: 3);
        var now = DateTimeOffset.UtcNow;

        history.Observe("key", "A", 100, now, options);
        history.Observe("key", "B", 100, now, options);
        var mixed = history.Observe("key", "B", 100, now, options);
        var stable = history.Observe("key", "B", 100, now, options);

        Assert.Equal(CacheRecommendation.ResponseChanged, mixed.Recommendation);
        Assert.Equal(CacheRecommendation.CacheCandidate, stable.Recommendation);
    }

    [Fact]
    public void ObservationLimit_PreventsAccumulatingTrafficBeyondRetainedHistory()
    {
        var history = new InMemoryResponseFingerprintHistory();
        var options = Options(limit: 3);
        options.MinimumObservationsPerKey = 4;

        for (var i = 0; i < 100; i++)
        {
            var result = history.Observe("key", "A", 100, DateTimeOffset.UtcNow, options);
            Assert.Equal(i == 0 ? CacheRecommendation.FirstObservation : CacheRecommendation.InsufficientTraffic,
                result.Recommendation);
        }
    }

    [Fact]
    public void ExpiredFingerprint_IsExcludedEvenWhenTimestampsArriveOutOfOrder()
    {
        var history = new InMemoryResponseFingerprintHistory();
        var options = Options(limit: 10);
        options.ObservationWindow = TimeSpan.FromMinutes(1);
        var now = DateTimeOffset.UtcNow;

        history.Observe("key", "B", 100, now, options);
        history.Observe("key", "A", 100, now.AddMinutes(-2), options);
        var result = history.Observe("key", "B", 100, now, options);

        Assert.Equal(CacheRecommendation.CacheCandidate, result.Recommendation);
    }

    [Fact]
    public void LoweredObservationLimit_TrimsExistingHistoryOnNextObservation()
    {
        var history = new InMemoryResponseFingerprintHistory();
        var options = Options(limit: 10);
        var now = DateTimeOffset.UtcNow;
        history.Observe("key", "A", 100, now, options);
        history.Observe("key", "B", 100, now, options);
        options.MaximumObservationsPerKey = 2;

        var result = history.Observe("key", "B", 100, now, options);

        Assert.Equal(CacheRecommendation.CacheCandidate, result.Recommendation);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidObservationLimit_IsRejected(int limit)
    {
        var history = new InMemoryResponseFingerprintHistory();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            history.Observe("key", "A", 100, DateTimeOffset.UtcNow, Options(limit)));
    }

    [Fact]
    public void DistinctCountAndClear_LeaveCapacityPolicyToCaller()
    {
        var history = new InMemoryResponseFingerprintHistory();
        var options = Options(limit: 3);
        options.MaximumDistinctRequestKeys = 10;

        Parallel.For(0, 500, i =>
        {
            history.Observe(i.ToString(), "A", 100, DateTimeOffset.UtcNow, options);
        });

        Assert.Equal(500, history.GetCountDistinctRequests());
        history.Clear();
        Assert.Equal(0, history.GetCountDistinctRequests());
        Assert.Equal(CacheRecommendation.FirstObservation,
            history.Observe("0", "B", 100, DateTimeOffset.UtcNow, options).Recommendation);
        Assert.Equal(1, history.GetCountDistinctRequests());
    }

    [Fact]
    public void ConcurrentObservations_RespectPerKeyLimitAndRemainIndependent()
    {
        var history = new InMemoryResponseFingerprintHistory();
        var options = Options(limit: 10);
        options.MinimumObservationsPerKey = 11;

        Parallel.For(0, 1_000, i =>
        {
            var result = history.Observe((i % 2).ToString(), "A", 100, DateTimeOffset.UtcNow, options);
            Assert.Contains(result.Recommendation,
                new[] { CacheRecommendation.FirstObservation, CacheRecommendation.InsufficientTraffic });
        });

        options.MinimumObservationsPerKey = 10;
        // Concurrent pruning may discard extra entries. Refill after writers finish;
        // recommendations need not reflect a transactionally consistent history.
        for (var i = 0; i < 10; i++)
        {
            history.Observe("0", "A", 100, DateTimeOffset.UtcNow, options);
            history.Observe("1", "A", 100, DateTimeOffset.UtcNow, options);
        }
        Assert.Equal(CacheRecommendation.CacheCandidate,
            history.Observe("0", "A", 100, DateTimeOffset.UtcNow, options).Recommendation);
        Assert.Equal(CacheRecommendation.ResponseChanged,
            history.Observe("1", "B", 100, DateTimeOffset.UtcNow, options).Recommendation);
        Assert.Equal(CacheRecommendation.CacheCandidate,
            history.Observe("0", "A", 100, DateTimeOffset.UtcNow, options).Recommendation);
    }

    private static CacheAnalysisOptions Options(int limit) => new()
    {
        MaximumObservationsPerKey = limit,
        MinimumObservationsPerKey = 2,
        MinimumPayloadToFingerprintRatio = 0
    };
}
