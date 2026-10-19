using CacheAnalyzer.Core.Models;
using CacheAnalyzer.Core.Services.Reporting;
using Microsoft.Extensions.Logging;

namespace CacheAnalyzer.AspNetCore.Services.Reporting;

internal sealed class LoggingResponseAnalysisSink : IResponseAnalysisSink
{
    private readonly ILogger<LoggingResponseAnalysisSink> _logger;

    public LoggingResponseAnalysisSink(ILogger<LoggingResponseAnalysisSink> logger)
    {
        _logger = logger;
    }

    public void Report(ResponseAnalysis analysis)
    {
        // Use one property type across all log events, including unknown findings.
        var cacheable = analysis.Recommendation switch
        {
            CacheRecommendation.CacheCandidate => "1",
            CacheRecommendation.StableDataCandidate => "partial",
            CacheRecommendation.ResponseChanged or
                CacheRecommendation.PayloadTooLarge or
                CacheRecommendation.FingerprintTooSlow => "0",
            _ => "unknown"
        };

        var level = analysis.Recommendation is CacheRecommendation.CacheCandidate or CacheRecommendation.StableDataCandidate or
            CacheRecommendation.PayloadTooLarge or CacheRecommendation.FingerprintTooSlow
                ? LogLevel.Information : LogLevel.Debug;

        // Compose only constant placeholders; never insert request values into a template.
        // Optional sensitive fields are absent from both message and structured state.
        var template = "Cache analysis for {Endpoint} (fingerprintRequest={fingerprintRequest}): cacheable={Cacheable}, {Recommendation}, {PayloadBytes} bytes, fingerprint generated in {FingerprintMilliseconds} ms, trackingCapacityReached={TrackingCapacityReached}";
        var values = new List<object?>
        {
            analysis.Endpoint,
            analysis.FingerprintRequest,
            cacheable,
            analysis.Recommendation.ToString(),
            analysis.PayloadBytes,
            analysis.FingerprintDuration.TotalMilliseconds,
            analysis.TrackingCapacityReached
        };
        template += ", fingerprintComparison={FingerprintComparison}";
        values.Add(analysis.FingerprintComparison);
        template += ", fingerprintBytes={FingerprintBytes}, payloadToFingerprintRatio={PayloadToFingerprintRatio}";
        values.Add(analysis.FingerprintBytes);
        values.Add(analysis.PayloadToFingerprintRatio);
        if (analysis.IncludeFingerprintInLogs)
        {
            template += ", fingerprintResponse={fingerprintResponse}";
            values.Add(analysis.FingerprintResponse);
        }
        if (analysis.IncludeFullRequestKeyInLogs)
        {
            template += ", fullRequestKey={FullRequestKey}";
            values.Add(analysis.FullRequestKey);
        }
        _logger.Log(level, template, values.ToArray());
    }
}
