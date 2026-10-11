using CacheAnalyzer.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace CacheAnalyzer.AspNetCore;

public static class CacheAnalyzerServiceCollectionExtensions
{
    public static IServiceCollection AddCacheAnalyzer(
        this IServiceCollection services,
        Action<CacheAnalysisOptions>? configure = null)
    {
        services.AddOptions<CacheAnalysisOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton<ICacheAnalyzerRuntimeControl, CacheAnalyzerRuntimeControl>();
        services.TryAddSingleton<CacheCandidateStore>();
        services.TryAddSingleton<IResponseFingerprintProvider, Sha256ResponseFingerprintProvider>();
        services.TryAddSingleton<IResponseFingerprintHistory, InMemoryResponseFingerprintHistory>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IResponseAnalysisSink, LoggingResponseAnalysisSink>());

        return services;
    }

    public static IApplicationBuilder UseCacheAnalyzer(this IApplicationBuilder app)
    {
        return app.UseMiddleware<CacheAnalyzerMiddleware>();
    }
}
