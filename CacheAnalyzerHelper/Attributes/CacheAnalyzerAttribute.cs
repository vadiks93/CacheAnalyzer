using Metalama.Framework.Aspects;

namespace CacheAnalyzerHelper.Attributes;

[Obsolete("Use CacheAnalyzeAttribute instead.")]
[AttributeUsage(AttributeTargets.Method)]
public sealed class CacheAnalyzerAttribute : CacheAnalyzeAttribute
{
}
