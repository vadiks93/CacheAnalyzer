using CacheAnalyzer.AspNetCore.Extensions;
using CacheAnalyzer.AspNetCore.Services.Runtime;
using CacheAnalyzer.Core.Options;
using CacheAnalyzer.Core.Services.Reporting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace CacheAnalyzerHelper.Tests.AspNetCore.Extensions;

public sealed class CacheAnalyzerRegistrationTests
{
    [Fact]
    public async Task PublicExtensions_ActivateInternalServicesAndMiddleware()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCacheAnalyzer(options => options.Enabled = false);
        using var provider = services.BuildServiceProvider();

        var control = provider.GetRequiredService<ICacheAnalyzerRuntimeControl>();
        Assert.True(control.IsEnabled);
        control.Disable();
        Assert.False(control.IsEnabled);
        control.Enable();
        Assert.True(control.IsEnabled);
        Assert.Single(provider.GetServices<IResponseAnalysisSink>());

        var app = new ApplicationBuilder(provider);
        app.UseCacheAnalyzer();
        app.Run(context =>
        {
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            return context.Response.WriteAsync("original body");
        });
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Method = HttpMethods.Get;
        using var body = new MemoryStream();
        context.Response.Body = body;

        await app.Build()(context);

        Assert.Equal(StatusCodes.Status202Accepted, context.Response.StatusCode);
        Assert.Equal("original body", System.Text.Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public void Adapter_ExportsOnlyClientEntryPoints()
    {
        var exported = typeof(CacheAnalyzerServiceCollectionExtensions).Assembly.GetExportedTypes();
        Assert.Equal(
            new[] { typeof(CacheAnalyzerServiceCollectionExtensions), typeof(ICacheAnalyzerRuntimeControl) }
                .OrderBy(type => type.FullName),
            exported.OrderBy(type => type.FullName));
    }
}
