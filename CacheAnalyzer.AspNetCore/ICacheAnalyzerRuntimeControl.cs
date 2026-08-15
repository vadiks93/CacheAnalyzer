namespace CacheAnalyzer.AspNetCore;

public interface ICacheAnalyzerRuntimeControl
{
    bool IsEnabled { get; }

    void Enable();

    void Disable();
}
