using CacheAnalyzer.Core;

namespace CacheAnalyzerHelper.Tests;

public sealed class SlowFingerprintExclusionsTests
{
    [Fact]
    public void Defaults_Allow500VariantsPerPathAnd500Groups()
    {
        var options = new CacheAnalysisOptions();
        Assert.Equal(500, options.MaximumSlowFingerprintRequestsPerPath);
        Assert.Equal(500, options.MaximumSlowFingerprintRequests);
    }

    [Fact]
    public void DistinctVariants_ExcludePathWhileRepeatsDoNotCount()
    {
        var exclusions = new SlowFingerprintExclusions();
        var options = new CacheAnalysisOptions { MaximumSlowFingerprintRequestsPerPath = 1 };
        const string path = "GET https://example.test/products";
        Assert.Equal(SlowFingerprintRecordResult.Recorded, exclusions.Record(path + "?id=1", path, options));
        for (var i = 0; i < 100; i++)
            Assert.Equal(SlowFingerprintRecordResult.Recorded, exclusions.Record(path + "?id=1", path, options));
        Assert.True(exclusions.IsExcluded(path + "?id=1", path, options));
        Assert.False(exclusions.IsExcluded(path + "?id=2", path, options));
        Assert.Equal(SlowFingerprintRecordResult.PathExcluded, exclusions.Record(path + "?id=2", path, options));
        Assert.True(exclusions.IsExcluded(path + "?id=3", path, options));
        Assert.False(exclusions.IsExcluded("GET https://other.test/products?id=3", "GET https://other.test/products", options));
        Assert.False(exclusions.IsExcluded(path + "/123", path + "/123", options));
    }

    [Fact]
    public void FullStore_KeepsExclusionsAndRequestsGlobalStopForAnotherGroup()
    {
        var exclusions = new SlowFingerprintExclusions();
        var options = new CacheAnalysisOptions
        {
            MaximumSlowFingerprintRequests = 2,
            MaximumSlowFingerprintRequestsPerPath = 1
        };
        Assert.Equal(SlowFingerprintRecordResult.Recorded, exclusions.Record("a?1", "path-a", options));
        Assert.Equal(SlowFingerprintRecordResult.Recorded, exclusions.Record("b", "path-b", options));
        Assert.Equal(SlowFingerprintRecordResult.PathExcluded, exclusions.Record("a?2", "path-a", options));
        Assert.Equal(SlowFingerprintRecordResult.Recorded, exclusions.Record("a?3", "path-a", options));
        Assert.Equal(SlowFingerprintRecordResult.CapacityExceeded, exclusions.Record("c", "path-c", options));
        Assert.True(exclusions.IsExcluded("a?new", "path-a", options));
        Assert.True(exclusions.IsExcluded("b", "path-b", options));
        Assert.False(exclusions.IsExcluded("c", "path-c", options));
        exclusions.Clear();
        Assert.False(exclusions.IsExcluded("a?new", "path-a", options));
        Assert.False(exclusions.IsExcluded("b", "path-b", options));
        Assert.Equal(SlowFingerprintRecordResult.Recorded, exclusions.Record("c", "path-c", options));
    }

    [Fact]
    public void ConcurrentDistinctPaths_EventuallyRequestGlobalStopAndClear()
    {
        var exclusions = new SlowFingerprintExclusions();
        var options = new CacheAnalysisOptions { MaximumSlowFingerprintRequests = 10 };
        Parallel.For(0, 100, i => exclusions.Record("request-" + i, "path-" + i, options));
        Assert.Equal(SlowFingerprintRecordResult.CapacityExceeded, exclusions.Record("new", "new-path", options));
        exclusions.Clear();
        for (var i = 0; i < 100; i++)
            Assert.False(exclusions.IsExcluded("request-" + i, "path-" + i, options));
    }

    [Fact]
    public void QueryBodyKeys_CountSeparatelyTowardOverallLimit()
    {
        var exclusions = new SlowFingerprintExclusions();
        var options = new CacheAnalysisOptions { MaximumSlowFingerprintRequests = 1 };
        Assert.Equal(SlowFingerprintRecordResult.Recorded, exclusions.Record("QUERY /products body-A", null, options));
        Assert.Equal(SlowFingerprintRecordResult.Recorded, exclusions.Record("QUERY /products body-A", null, options));
        Assert.Equal(SlowFingerprintRecordResult.CapacityExceeded, exclusions.Record("QUERY /products body-B", null, options));
    }

    [Fact]
    public void QueryRequests_ExcludeOnlyExactBodyKeys()
    {
        var exclusions = new SlowFingerprintExclusions();
        var options = new CacheAnalysisOptions { MaximumSlowFingerprintRequestsPerPath = 1 };
        Assert.Equal(SlowFingerprintRecordResult.Recorded, exclusions.Record("QUERY /products body-A", null, options));
        Assert.True(exclusions.IsExcluded("QUERY /products body-A", null, options));
        Assert.False(exclusions.IsExcluded("QUERY /products body-B", null, options));
    }

    [Fact]
    public void ConcurrentSlowVariants_EventuallyExcludeOnlyTheirPathAndClear()
    {
        var exclusions = new SlowFingerprintExclusions();
        var options = new CacheAnalysisOptions { MaximumSlowFingerprintRequestsPerPath = 10 };
        Parallel.For(0, 100, i => exclusions.Record("path?id=" + i, "path", options));
        Assert.True(exclusions.IsExcluded("path?id=new", "path", options));
        Assert.False(exclusions.IsExcluded("other?id=new", "other", options));
        exclusions.Clear();
        Assert.False(exclusions.IsExcluded("path?id=new", "path", options));
    }
}
