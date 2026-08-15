using CacheAnalyzer.Core;
using Microsoft.Extensions.Logging;

namespace CacheAnalyzer.AspNetCore;

public sealed class LoggingResponseAnalysisSink : IResponseAnalysisSink
{
    private readonly ILogger<LoggingResponseAnalysisSink> _logger;

    public LoggingResponseAnalysisSink(ILogger<LoggingResponseAnalysisSink> logger)
    {
        _logger = logger;
    }

    public void Report(ResponseAnalysis analysis)
    {
        if (analysis.Recommendation == CacheRecommendation.CacheCandidate)
        {
            _logger.LogInformation(
                "Cache candidate detected for {RequestKey}: {PayloadBytes} bytes, fingerprint generated in {FingerprintMilliseconds} ms",
                analysis.RequestKey,
                analysis.PayloadBytes,
                analysis.FingerprintDuration.TotalMilliseconds);
            return;
        }

        if (analysis.Recommendation is CacheRecommendation.PayloadTooLarge or CacheRecommendation.FingerprintTooSlow)
        {
            _logger.LogInformation(
                "Caching is not recommended for {RequestKey}: {Recommendation}, {PayloadBytes} bytes, fingerprint generated in {FingerprintMilliseconds} ms",
                analysis.RequestKey,
                analysis.Recommendation,
                analysis.PayloadBytes,
                analysis.FingerprintDuration.TotalMilliseconds);
            return;
        }

        _logger.LogDebug(
            "Cache analysis for {RequestKey}: {Recommendation}, {PayloadBytes} bytes",
            analysis.RequestKey,
            analysis.Recommendation,
            analysis.PayloadBytes);
    }
}
