using CacheAnalyzer.AspNetCore.Services.Reporting;
using CacheAnalyzer.Core.Models;
using CacheAnalyzer.Core.Services.Fingerprinting;
using Microsoft.Extensions.Logging;

namespace CacheAnalyzerHelper.Tests.AspNetCore.Services.Reporting;

public sealed class LoggingResponseAnalysisSinkTests
{
    [Theory]
    [InlineData(CacheRecommendation.CacheCandidate, "1", LogLevel.Information)]
    [InlineData(CacheRecommendation.ResponseChanged, "0", LogLevel.Debug)]
    [InlineData(CacheRecommendation.PayloadTooLarge, "0", LogLevel.Information)]
    [InlineData(CacheRecommendation.FingerprintTooSlow, "0", LogLevel.Information)]
    [InlineData(CacheRecommendation.FirstObservation, "unknown", LogLevel.Debug)]
    [InlineData(CacheRecommendation.InsufficientTraffic, "unknown", LogLevel.Debug)]
    public void Report_EmitsStructuredCacheableStatus(
        CacheRecommendation recommendation, string expectedStatus, LogLevel expectedLevel)
    {
        var logger = new CapturingLogger();
        new LoggingResponseAnalysisSink(logger).Report(
            new ResponseAnalysis("GET /products", recommendation, 1024, TimeSpan.FromMilliseconds(2), "internal-hash"));

        Assert.Equal(expectedLevel, logger.Level);
        Assert.Equal(expectedStatus, logger.Properties["Cacheable"]);
        Assert.Equal(RequestIdentity.Create("GET /products"), logger.Properties["fingerprintRequest"]);
        Assert.Equal("(unmapped endpoint)", logger.Properties["Endpoint"]);
        Assert.False(logger.Properties.ContainsKey("RequestKey"));
        Assert.False(logger.Properties.ContainsKey("RequestId"));
        Assert.False(logger.Properties.ContainsKey("Fingerprint"));
        Assert.False(logger.Properties.ContainsKey("FullRequestKey"));
        Assert.DoesNotContain("GET /products", logger.Message);
        Assert.Equal(recommendation.ToString(), logger.Properties["Recommendation"]);
        Assert.Equal(1024L, logger.Properties["PayloadBytes"]);
        Assert.Equal(2d, logger.Properties["FingerprintMilliseconds"]);
        Assert.False(logger.Properties.ContainsKey("fingerprintResponse"));
        Assert.DoesNotContain("internal-hash", logger.Message);
        Assert.DoesNotContain("fingerprintResponse={fingerprintResponse}", logger.Properties["{OriginalFormat}"]?.ToString() ?? "");
        Assert.Equal(false, logger.Properties["TrackingCapacityReached"]);
        Assert.Contains("cacheable=" + expectedStatus, logger.Message);
    }

    [Fact]
    public void ExplicitOptIn_IncludesFingerprintInStructuredStateAndMessage()
    {
        var logger = new CapturingLogger();
        new LoggingResponseAnalysisSink(logger).Report(
            new ResponseAnalysis("GET /products", CacheRecommendation.CacheCandidate, 1024,
                TimeSpan.FromMilliseconds(2), "internal-hash", includeFingerprintInLogs: true));

        Assert.Equal("internal-hash", logger.Properties["fingerprintResponse"]);
        Assert.Contains("fingerprintResponse=internal-hash", logger.Message);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void DisclosureOptions_IndependentlyControlSensitiveFields(bool fullRequest, bool fingerprint)
    {
        const string fullKey = "GET https://private.example/products/secret-id?token=private-token";
        var logger = new CapturingLogger();
        var analysis = new ResponseAnalysis(fullKey, CacheRecommendation.CacheCandidate, 1024,
            TimeSpan.FromMilliseconds(2), "private-fingerprint", includeFingerprintInLogs: fingerprint,
            endpoint: "GET /products/{id}", includeFullRequestKeyInLogs: fullRequest);
        new LoggingResponseAnalysisSink(logger).Report(analysis);

        Assert.Equal("GET /products/{id}", logger.Properties["Endpoint"]);
        Assert.Equal(RequestIdentity.Create(fullKey), logger.Properties["fingerprintRequest"]);
        Assert.Equal(fullRequest, logger.Properties.ContainsKey("FullRequestKey"));
        Assert.Equal(fingerprint, logger.Properties.ContainsKey("fingerprintResponse"));
        Assert.Equal(fullRequest, logger.Message.Contains("private-token"));
        Assert.Equal(fingerprint, logger.Message.Contains("private-fingerprint"));
        Assert.Equal(fullRequest ? fullKey : null, analysis.FullRequestKey);
        if (fullRequest) Assert.Equal(fullKey, logger.Properties["FullRequestKey"]);
    }

    private sealed class CapturingLogger : ILogger<LoggingResponseAnalysisSink>
    {
        public LogLevel Level { get; private set; }
        public string Message { get; private set; } = "";
        public Dictionary<string, object?> Properties { get; private set; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Level = logLevel;
            Message = formatter(state, exception);
            Properties = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state)
                .ToDictionary(pair => pair.Key, pair => pair.Value);
        }
    }
}
