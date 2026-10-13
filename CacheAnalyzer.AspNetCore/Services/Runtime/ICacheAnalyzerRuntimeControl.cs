namespace CacheAnalyzer.AspNetCore.Services.Runtime;

public interface ICacheAnalyzerRuntimeControl
{
    bool IsEnabled { get; }

    void Enable();

    void Disable();
}
