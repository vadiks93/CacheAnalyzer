namespace CacheAnalyzerHelper.Attributes;

[AttributeUsage(AttributeTargets.Method)]
public class CacheAnalyzeAttribute : Attribute
{
    /// <summary>Opt in to logging the response fingerprint for this endpoint.</summary>
    public bool IncludeFingerprintInLogs { get; set; }

    /// <summary>Opt in to retaining and logging the full request key for this endpoint.</summary>
    public bool IncludeFullRequestKeyInLogs { get; set; }

}

