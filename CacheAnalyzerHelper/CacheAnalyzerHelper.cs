using Metalama.Framework.Aspects;
using Metalama.Framework.Code;

namespace CacheAnalyzerHelper;

[AttributeUsage(AttributeTargets.Method)]
public class CacheAnalyzeAttribute : Attribute, IAspect<IMethod>
{
    public void BuildAspect(IAspectBuilder<IMethod> builder)
    {
        // Framework adapters consume this method metadata at runtime.
    }
}

[Obsolete("Use CacheAnalyzeAttribute instead.")]
[AttributeUsage(AttributeTargets.Method)]
public sealed class CacheAnalyzerAttribute : CacheAnalyzeAttribute
{
}
