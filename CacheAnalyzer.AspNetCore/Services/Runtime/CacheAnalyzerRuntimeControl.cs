namespace CacheAnalyzer.AspNetCore.Services.Runtime;

internal sealed class CacheAnalyzerRuntimeControl : ICacheAnalyzerRuntimeControl
{
    private volatile bool _isEnabled = true;

    public bool IsEnabled => _isEnabled;

    public void Enable()
    {
        _isEnabled = true;
    }

    public void Disable()
    {
        _isEnabled = false;
    }
}
