using CacheAnalyzer.Core.Services.Fingerprinting;
namespace CacheAnalyzer.Core.Models;

public sealed class ResponseAnalysis
{
    public ResponseAnalysis(
        string requestKey,
        CacheRecommendation recommendation,
        long payloadBytes,
        TimeSpan fingerprintDuration,
        string? fingerprintResponse,
        bool trackingCapacityReached = false,
        bool includeFingerprintInLogs = false,
        string? endpoint = null,
        bool includeFullRequestKeyInLogs = false)
    {
        FingerprintRequest = RequestIdentity.Create(requestKey);
        Endpoint = endpoint ?? "(unmapped endpoint)";
        FullRequestKey = includeFullRequestKeyInLogs ? requestKey : null;
        Recommendation = recommendation;
        PayloadBytes = payloadBytes;
        FingerprintDuration = fingerprintDuration;
        FingerprintResponse = fingerprintResponse;
        TrackingCapacityReached = trackingCapacityReached;
        IncludeFingerprintInLogs = includeFingerprintInLogs;
        IncludeFullRequestKeyInLogs = includeFullRequestKeyInLogs;
    }

    /// <summary>Opaque request identity. Use Endpoint for a readable label.</summary>
    public string RequestKey => FingerprintRequest;

    public string FingerprintRequest { get; }
    public string Endpoint { get; }
    /// <summary>Full request details retained only when explicitly opted in.</summary>
    public string? FullRequestKey { get; }
    public bool IncludeFullRequestKeyInLogs { get; }

    public CacheRecommendation Recommendation { get; }

    public long PayloadBytes { get; }

    public TimeSpan FingerprintDuration { get; }

    public string? FingerprintResponse { get; }

    public bool TrackingCapacityReached { get; }

    /// <summary>Whether the default logging sink may include the internal fingerprint.</summary>
    public bool IncludeFingerprintInLogs { get; }
}
