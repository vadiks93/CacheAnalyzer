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
        bool includeFullRequestKeyInLogs = false,
        string fingerprintComparison = "ExactBytes")
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
        FingerprintComparison = fingerprintComparison;
    }

    /// <summary>Opaque request identity. Use Endpoint for a readable label.</summary>
    public string RequestKey => FingerprintRequest;

    public string FingerprintRequest { get; }
    public string Endpoint { get; }
    /// <summary>Full request details retained only when explicitly opted in.</summary>
    public string? FullRequestKey { get; }
    public bool IncludeFullRequestKeyInLogs { get; }

    public CacheRecommendation Recommendation { get; }

    /// <summary>ExactBytes, JsonExclusionsApplied, or JsonExclusionsNotApplied (exact fallback).</summary>
    public string FingerprintComparison { get; }

    public long PayloadBytes { get; }

    public TimeSpan FingerprintDuration { get; }

    public string? FingerprintResponse { get; }

    /// <summary>Stored fingerprint representation length used by the built-in ratio check,
    /// including the comparison-mode prefix for JSON exclusions. Not raw SHA-256 bytes.</summary>
    public int? FingerprintBytes => string.IsNullOrEmpty(FingerprintResponse) ? (int?)null :
        FingerprintResponse!.Length + (FingerprintComparison == "ExactBytes" ? 0 : FingerprintComparison.Length + 1);

    /// <summary>Original payload size divided by stored fingerprint size; null when no fingerprint exists.</summary>
    public double? PayloadToFingerprintRatio => FingerprintBytes is int size ? PayloadBytes / (double)size : (double?)null;

    public bool TrackingCapacityReached { get; }

    /// <summary>Whether the default logging sink may include the internal fingerprint.</summary>
    public bool IncludeFingerprintInLogs { get; }
}
