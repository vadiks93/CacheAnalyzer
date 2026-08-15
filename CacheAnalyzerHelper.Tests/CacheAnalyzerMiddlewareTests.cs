using System.Text;
using CacheAnalyzer.AspNetCore;
using CacheAnalyzer.Core;
using CacheAnalyzerHelper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CacheAnalyzerHelper.Tests;

public sealed class CacheAnalyzerMiddlewareTests
{
    private const string ResponseJson = "{\"id\":42,\"name\":\"cached value\"}";

    [Fact]
    public async Task TaggedGet_AddsETagAndReturnsResponse()
    {
        var context = CreateContext();
        var middleware = CreateMiddleware();

        await InvokeAsync(middleware, context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(context.Response.Headers.ETag));
        Assert.Equal(ResponseJson, ReadBody(context));
    }

    [Fact]
    public async Task TaggedConditionalGet_WithMatchingETag_Returns304WithoutBody()
    {
        var provider = new Sha256ResponseFingerprintProvider();
        var etag = provider.CreateFingerprint(Encoding.UTF8.GetBytes(ResponseJson));
        var context = CreateContext(etag);
        var middleware = CreateMiddleware();

        await InvokeAsync(middleware, context, provider);

        Assert.Equal(StatusCodes.Status304NotModified, context.Response.StatusCode);
        Assert.Equal(etag, context.Response.Headers.ETag);
        Assert.Empty(((MemoryStream)context.Response.Body).ToArray());
    }

    [Fact]
    public async Task UntaggedGet_DoesNotAddETag()
    {
        var context = CreateContext(tagged: false);
        var middleware = CreateMiddleware();

        await InvokeAsync(middleware, context);

        Assert.False(context.Response.Headers.ContainsKey("ETag"));
        Assert.Equal(ResponseJson, ReadBody(context));
    }

    [Fact]
    public async Task AllGetMode_AnalyzesUntaggedGet()
    {
        var context = CreateContext(tagged: false);
        var options = new CacheAnalysisOptions
        {
            EndpointSelection = EndpointSelectionMode.AllGetAndHeadRequests
        };

        await InvokeAsync(CreateMiddleware(), context, options: options);

        Assert.False(string.IsNullOrWhiteSpace(context.Response.Headers.ETag));
    }

    [Fact]
    public async Task DisabledOption_BypassesAnalysis()
    {
        var context = CreateContext();
        var options = new CacheAnalysisOptions { Enabled = false };

        await InvokeAsync(CreateMiddleware(), context, options: options);

        Assert.False(context.Response.Headers.ContainsKey("ETag"));
        Assert.Equal(ResponseJson, ReadBody(context));
    }

    [Fact]
    public async Task RuntimeControl_BypassesAndCanReEnableAnalysis()
    {
        var runtimeControl = new CacheAnalyzerRuntimeControl();
        runtimeControl.Disable();
        var disabledContext = CreateContext();

        await InvokeAsync(CreateMiddleware(), disabledContext, runtimeControl: runtimeControl);

        Assert.False(disabledContext.Response.Headers.ContainsKey("ETag"));

        runtimeControl.Enable();
        var enabledContext = CreateContext();
        await InvokeAsync(CreateMiddleware(), enabledContext, runtimeControl: runtimeControl);

        Assert.False(string.IsNullOrWhiteSpace(enabledContext.Response.Headers.ETag));
    }

    [Fact]
    public async Task RepeatedResponse_WithoutConditionalHeader_ReportsCacheCandidate()
    {
        var middleware = CreateMiddleware();
        var history = new InMemoryResponseFingerprintHistory();
        var sink = new CollectingSink();
        var options = new CacheAnalysisOptions
        {
            MinimumObservations = 2,
            MinimumPayloadToFingerprintRatio = 0
        };

        await InvokeAsync(middleware, CreateContext(), history: history, options: options, sinks: [sink]);
        await InvokeAsync(middleware, CreateContext(), history: history, options: options, sinks: [sink]);

        Assert.Collection(
            sink.Analyses,
            first => Assert.Equal(CacheRecommendation.FirstObservation, first.Recommendation),
            second => Assert.Equal(CacheRecommendation.CacheCandidate, second.Recommendation));
    }

    [Fact]
    public void FrequentSmallResponse_IsNotRecommendedWhenRatioIsTooLow()
    {
        var history = new InMemoryResponseFingerprintHistory();
        var options = new CacheAnalysisOptions
        {
            MinimumObservations = 2,
            MinimumPayloadToFingerprintRatio = 10
        };
        var now = DateTimeOffset.UtcNow;

        history.Observe("GET /small", "\"45-byte-fingerprint-value-placeholder-1234567\"", 100, now, options);
        var result = history.Observe(
            "GET /small",
            "\"45-byte-fingerprint-value-placeholder-1234567\"",
            100,
            now.AddMinutes(1),
            options);

        Assert.Equal(CacheRecommendation.InsufficientTraffic, result.Recommendation);
    }

    [Fact]
    public void NewRequestKey_ReportsCapacityWithoutReplacingRecommendation()
    {
        var history = new InMemoryResponseFingerprintHistory();
        var options = new CacheAnalysisOptions { MaximumTrackedRequests = 1 };
        var now = DateTimeOffset.UtcNow;

        history.Observe("GET /first", "\"first\"", 1_000, now, options);
        var result = history.Observe("GET /second", "\"second\"", 1_000, now, options);

        Assert.Equal(CacheRecommendation.InsufficientTraffic, result.Recommendation);
        Assert.True(result.TrackingCapacityReached);
    }

    [Fact]
    public async Task PayloadOverConfiguredLimit_IsNotHashedOrTagged()
    {
        var context = CreateContext();
        var middleware = CreateMiddleware();
        var sink = new CollectingSink();
        var options = new CacheAnalysisOptions { MaximumPayloadBytes = 5 };

        await InvokeAsync(middleware, context, options: options, sinks: [sink]);

        Assert.False(context.Response.Headers.ContainsKey("ETag"));
        Assert.Equal(CacheRecommendation.PayloadTooLarge, Assert.Single(sink.Analyses).Recommendation);
        Assert.Equal(ResponseJson, ReadBody(context));
    }

    [Fact]
    public async Task SlowFingerprint_IsReportedAndDoesNotEmitETag()
    {
        var context = CreateContext();
        var middleware = CreateMiddleware();
        var sink = new CollectingSink();
        var options = new CacheAnalysisOptions
        {
            MaximumFingerprintDuration = TimeSpan.FromMilliseconds(1)
        };

        await InvokeAsync(
            middleware,
            context,
            provider: new SlowFingerprintProvider(),
            options: options,
            sinks: [sink]);

        Assert.False(context.Response.Headers.ContainsKey("ETag"));
        Assert.Equal(CacheRecommendation.FingerprintTooSlow, Assert.Single(sink.Analyses).Recommendation);
        Assert.Equal(ResponseJson, ReadBody(context));
    }

    private static CacheAnalyzerMiddleware CreateMiddleware()
    {
        return new CacheAnalyzerMiddleware(async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(ResponseJson);
        });
    }

    private static DefaultHttpContext CreateContext(string? etag = null, bool tagged = true)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Response.Body = new MemoryStream();

        if (etag is not null)
        {
            context.Request.Headers.IfNoneMatch = etag;
        }

        var metadata = tagged
            ? new EndpointMetadataCollection(new CacheAnalyzeAttribute())
            : new EndpointMetadataCollection();

        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, metadata, "test endpoint"));
        return context;
    }

    private static string ReadBody(DefaultHttpContext context)
    {
        return Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
    }

    private static Task InvokeAsync(
        CacheAnalyzerMiddleware middleware,
        DefaultHttpContext context,
        IResponseFingerprintProvider? provider = null,
        IResponseFingerprintHistory? history = null,
        CacheAnalysisOptions? options = null,
        IEnumerable<IResponseAnalysisSink>? sinks = null,
        ICacheAnalyzerRuntimeControl? runtimeControl = null)
    {
        var configuredOptions = options ?? new CacheAnalysisOptions();
        return middleware.InvokeAsync(
            context,
            provider ?? new Sha256ResponseFingerprintProvider(),
            history ?? new InMemoryResponseFingerprintHistory(),
            new StaticOptionsMonitor<CacheAnalysisOptions>(configuredOptions),
            runtimeControl ?? new CacheAnalyzerRuntimeControl(),
            sinks ?? [],
            NullLogger<CacheAnalyzerMiddleware>.Instance);
    }

    private sealed class CollectingSink : IResponseAnalysisSink
    {
        public List<ResponseAnalysis> Analyses { get; } = [];

        public void Report(ResponseAnalysis analysis)
        {
            Analyses.Add(analysis);
        }
    }

    private sealed class SlowFingerprintProvider : IResponseFingerprintProvider
    {
        private readonly Sha256ResponseFingerprintProvider _inner = new();

        public string CreateFingerprint(byte[] responseBody)
        {
            Thread.Sleep(10);
            return _inner.CreateFingerprint(responseBody);
        }
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value)
        {
            CurrentValue = value;
        }

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
