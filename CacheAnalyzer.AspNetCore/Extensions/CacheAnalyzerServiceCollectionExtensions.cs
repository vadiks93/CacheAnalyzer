using CacheAnalyzer.AspNetCore.Middleware;
using CacheAnalyzer.AspNetCore.Services.Reporting;
using CacheAnalyzer.AspNetCore.Services.Runtime;
using CacheAnalyzer.Core.Options;
using CacheAnalyzer.Core.Services.Candidates;
using CacheAnalyzer.Core.Services.Fingerprinting;
using CacheAnalyzer.Core.Services.History;
using CacheAnalyzer.Core.Services.Reporting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace CacheAnalyzer.AspNetCore.Extensions;

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
