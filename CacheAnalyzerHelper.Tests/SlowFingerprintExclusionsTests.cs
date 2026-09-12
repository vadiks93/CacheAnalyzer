using CacheAnalyzer.Core;

namespace CacheAnalyzerHelper.Tests;

public sealed class SlowFingerprintExclusionsTests
{
    [Fact]
    public void Defaults_Allow500SlowKeysAnd50VariantsPerPath()
    {
        var options = new CacheAnalysisOptions();
        Assert.Equal(500, options.MaximumSlowFingerprintRequests);
        Assert.Equal(50, options.SlowFingerprintVariantsPerPath);
    }

    [Fact]
    public void DistinctVariants_ExcludePathWhileRepeatsDoNotCount()
    {
        var exclusions = new SlowFingerprintExclusions();
        var options = new CacheAnalysisOptions { SlowFingerprintVariantsPerPath = 2 };
        const string path = "GET https://example.test/products";
        Assert.False(exclusions.Record(path + "?id=1", path, options));
        for (var i = 0; i < 100; i++)
            Assert.False(exclusions.Record(path + "?id=1", path, options));
        Assert.True(exclusions.IsExcluded(path + "?id=1", path, options));
        Assert.False(exclusions.IsExcluded(path + "?id=2", path, options));
        Assert.False(exclusions.Record(path + "?id=2", path, options));
        Assert.True(exclusions.IsExcluded(path + "?id=3", path, options));
        Assert.False(exclusions.IsExcluded("GET https://other.test/products?id=3", "GET https://other.test/products", options));
        Assert.False(exclusions.IsExcluded(path + "/123", path + "/123", options));
    }

    [Fact]
    public void FullStore_RequestsShutdownForAnotherDistinctSlowKey()
    {
        var exclusions = new SlowFingerprintExclusions();
        var options = new CacheAnalysisOptions { MaximumSlowFingerprintRequests = 2 };
        Assert.False(exclusions.Record("a", "path-a", options));
        Assert.False(exclusions.Record("b", "path-b", options));
        Assert.False(exclusions.Record("a", "path-a", options));
        Assert.True(exclusions.Record("c", "path-c", options));
        Assert.False(exclusions.IsExcluded("c", "path-c", options));
        exclusions.Clear();
        Assert.False(exclusions.IsExcluded("a", "path-a", options));
        Assert.False(exclusions.Record("c", "path-c", options));
    }

    [Fact]
    public void QueryRequests_ExcludeOnlyExactBodyKeys()
    {
        var exclusions = new SlowFingerprintExclusions();
        var options = new CacheAnalysisOptions { SlowFingerprintVariantsPerPath = 1 };
        Assert.False(exclusions.Record("QUERY /products body-A", null, options));
        Assert.True(exclusions.IsExcluded("QUERY /products body-A", null, options));
        Assert.False(exclusions.IsExcluded("QUERY /products body-B", null, options));
    }

    [Fact]
    public void ConcurrentSlowKeys_EventuallyReachLimitAndClear()
    {
        var exclusions = new SlowFingerprintExclusions();
        var options = new CacheAnalysisOptions { MaximumSlowFingerprintRequests = 10 };
        Parallel.For(0, 100, i => exclusions.Record(i.ToString(), "path-" + i, options));
        Assert.True(exclusions.Record("new", "new-path", options));
        exclusions.Clear();
        for (var i = 0; i < 100; i++)
            Assert.False(exclusions.IsExcluded(i.ToString(), "path-" + i, options));
    }
}
