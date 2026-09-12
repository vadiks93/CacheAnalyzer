using System.Text;
using CacheAnalyzer.AspNetCore;
using CacheAnalyzer.Core;
using CacheAnalyzerHelper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CacheAnalyzerHelper.Tests;

public sealed class CacheAnalyzerMiddlewareTests
{
    private const string ResponseJson = "{\"id\":42,\"name\":\"cached value\"}";

    [Theory]
    [InlineData("GET")]
    [InlineData("QUERY")]
    public async Task TaggedSupportedMethod_ReportsWithoutAlteringResponse(string method)
    {
        var context = CreateContext();
        context.Request.Method = method;
        var middleware = CreateMiddleware();

        var sink = new CollectingSink();
        await InvokeAsync(middleware, context, sinks: [sink]);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("ETag"));
        Assert.Null(context.Response.ContentLength);
        Assert.Equal("application/json", context.Response.ContentType);
        Assert.Single(sink.Analyses);
        Assert.Equal(ResponseJson, ReadBody(context));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("QUERY")]
    public async Task TaggedConditionalRequest_WithMatchingFingerprint_ReturnsOriginalBody(string method)
    {
        var provider = new Sha256ResponseFingerprintProvider();
        var etag = provider.CreateFingerprint(Encoding.UTF8.GetBytes(ResponseJson));
        var context = CreateContext(etag);
        context.Request.Method = method;
        var middleware = CreateMiddleware();

        await InvokeAsync(middleware, context, provider);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("ETag"));
        Assert.Equal("application/json", context.Response.ContentType);
        Assert.Equal(ResponseJson, ReadBody(context));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("QUERY")]
    public async Task UntaggedSupportedMethod_DoesNotAddETag(string method)
    {
        var context = CreateContext(tagged: false);
        context.Request.Method = method;
        var middleware = CreateMiddleware();

        await InvokeAsync(middleware, context);

        Assert.False(context.Response.Headers.ContainsKey("ETag"));
        Assert.Equal(ResponseJson, ReadBody(context));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("QUERY")]
    public async Task AllGetAndQueryMode_AnalyzesUntaggedRequest(string method)
    {
        var context = CreateContext(tagged: false);
        context.Request.Method = method;
        var options = new CacheAnalysisOptions
        {
            EndpointSelection = EndpointSelectionMode.AllGetAndQueryRequests
        };

        var sink = new CollectingSink();
        await InvokeAsync(CreateMiddleware(), context, options: options, sinks: [sink]);

        Assert.Single(sink.Analyses);
        Assert.False(context.Response.Headers.ContainsKey("ETag"));
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
        var sink = new CollectingSink();

        await InvokeAsync(CreateMiddleware(), disabledContext, runtimeControl: runtimeControl, sinks: [sink]);

        Assert.False(disabledContext.Response.Headers.ContainsKey("ETag"));
        Assert.Empty(sink.Analyses);

        runtimeControl.Enable();
        var enabledContext = CreateContext();
        await InvokeAsync(CreateMiddleware(), enabledContext, runtimeControl: runtimeControl, sinks: [sink]);

        Assert.False(enabledContext.Response.Headers.ContainsKey("ETag"));
        Assert.Single(sink.Analyses);
    }

    [Fact]
    public async Task RepeatedResponse_WithoutConditionalHeader_ReportsCacheCandidate()
    {
        var middleware = CreateMiddleware();
        var history = new InMemoryResponseFingerprintHistory();
        var sink = new CollectingSink();
        var options = new CacheAnalysisOptions
        {
            MinimumObservationsPerKey = 2,
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
            MinimumObservationsPerKey = 2,
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
    public async Task CapacityReached_LogsOnceClearsHistoryAndPermanentlyBypasses()
    {
        var history = new InMemoryResponseFingerprintHistory();
        var options = new CacheAnalysisOptions { MaximumDistinctRequestKeys = 1 };
        var logger = new CollectingLogger();
        var sink = new CollectingSink();
        var bypassExpected = false;
        var middleware = new CacheAnalyzerMiddleware(async ctx =>
        {
            if (bypassExpected) Assert.Same(ctx.Items["originalBody"], ctx.Response.Body);
            ctx.Response.Headers.ETag = "\"application-owned\"";
            await ctx.Response.WriteAsync(ResponseJson);
        });

        await InvokeAsync(middleware, CreateContext(), history: history, options: options, sinks: [sink], logger: logger);
        Assert.Equal(0, history.GetCountDistinctRequests());
        Assert.Single(sink.Analyses);

        bypassExpected = true;
        options.MaximumDistinctRequestKeys = 100;
        for (var i = 0; i < 100; i++)
        {
            var context = CreateContext();
            context.Request.Method = "QUERY";
            context.Items["originalBody"] = context.Response.Body;
            await InvokeAsync(middleware, context, provider: new ThrowingFingerprintProvider(),
                history: history, options: options, sinks: [sink], logger: logger);
            Assert.Equal(ResponseJson, ReadBody(context));
            Assert.Equal("\"application-owned\"", context.Response.Headers.ETag);
        }

        Assert.Equal(0, history.GetCountDistinctRequests());
        Assert.Single(sink.Analyses);
        Assert.Equal(LogLevel.Information, Assert.Single(logger.Levels));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task FullHistory_BypassesBeforeBufferingOrObservation(int limit)
    {
        var history = new InMemoryResponseFingerprintHistory();
        var options = new CacheAnalysisOptions { MaximumDistinctRequestKeys = limit };
        if (limit > 0) history.Observe("existing", "A", 100, DateTimeOffset.UtcNow, options);
        var context = CreateContext();
        var originalBody = context.Response.Body;
        var sink = new CollectingSink();
        var logger = new CollectingLogger();
        var middleware = new CacheAnalyzerMiddleware(async ctx =>
        {
            Assert.Same(originalBody, ctx.Response.Body);
            await ctx.Response.WriteAsync(ResponseJson);
        });

        await InvokeAsync(middleware, context, provider: new ThrowingFingerprintProvider(),
            history: history, options: options, sinks: [sink], logger: logger);

        Assert.Equal(ResponseJson, ReadBody(context));
        Assert.Empty(sink.Analyses);
        Assert.Equal(0, history.GetCountDistinctRequests());
        Assert.Equal(LogLevel.Information, Assert.Single(logger.Levels));
    }

    [Fact]
    public async Task ConcurrentCapacityShutdown_InFlightRequestsDoNotRepopulateHistory()
    {
        var history = new InMemoryResponseFingerprintHistory();
        var options = new CacheAnalysisOptions { MaximumDistinctRequestKeys = 1 };
        var logger = new CollectingLogger();
        var sink = new CollectingSink();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var middleware = new CacheAnalyzerMiddleware(async ctx =>
        {
            if (ctx.Request.Path == "/paused")
            {
                entered.SetResult();
                await release.Task;
            }
            await ctx.Response.WriteAsync(ResponseJson);
        });
        var paused = CreateContext();
        paused.Request.Path = "/paused";
        var pending = InvokeAsync(middleware, paused, provider: new ThrowingFingerprintProvider(),
            history: history, options: options, sinks: [sink], logger: logger);
        await entered.Task;
        try
        {
            await InvokeAsync(middleware, CreateContext(), history: history, options: options, sinks: [sink], logger: logger);
        }
        finally
        {
            release.SetResult();
        }
        await pending;

        Assert.Equal(ResponseJson, ReadBody(paused));
        Assert.Equal(0, history.GetCountDistinctRequests());
        Assert.Single(sink.Analyses);
        Assert.Equal(LogLevel.Information, Assert.Single(logger.Levels));
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

    [Theory]
    [InlineData("HEAD", EndpointSelectionMode.AttributedEndpoints)]
    [InlineData("HEAD", EndpointSelectionMode.AllGetAndQueryRequests)]
    [InlineData("POST", EndpointSelectionMode.AllGetAndQueryRequests)]
    public async Task UnsupportedMethod_PreservesResponseWithoutAnalysis(string method, EndpointSelectionMode selection)
    {
        var context = CreateContext();
        context.Request.Method = method;
        var sink = new CollectingSink();
        var middleware = new CacheAnalyzerMiddleware(ctx =>
        {
            ctx.Response.ContentLength = 123;
            ctx.Response.Headers.ETag = "\"original\"";
            return Task.CompletedTask;
        });

        await InvokeAsync(middleware, context, options: new() { EndpointSelection = selection }, sinks: [sink]);

        Assert.Equal(123L, context.Response.ContentLength);
        Assert.Equal("\"original\"", context.Response.Headers.ETag);
        Assert.Empty(sink.Analyses);
    }

    [Fact]
    public async Task Query_SeparatesBodiesAndMetadataAndPreservesEndpointInput()
    {
        var history = new InMemoryResponseFingerprintHistory();
        var sink = new CollectingSink();
        var options = new CacheAnalysisOptions { MinimumObservationsPerKey = 2, MinimumPayloadToFingerprintRatio = 0 };
        foreach (var (body, contentType) in new[]
        {
            ("first", "text/plain"), ("second", "text/plain"),
            ("first", "application/example"), ("first", "text/plain")
        })
        {
            var context = CreateContext();
            context.Request.Method = "QUERY";
            context.Request.ContentType = contentType;
            context.Request.ContentLength = Encoding.UTF8.GetByteCount(body);
            context.Request.Headers.IfNoneMatch = "*";
            context.Request.Headers["X-Input"] = "unchanged";
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            var middleware = new CacheAnalyzerMiddleware(async ctx =>
            {
                Assert.Equal("QUERY", ctx.Request.Method);
                Assert.Equal(contentType, ctx.Request.ContentType);
                Assert.Equal((long)Encoding.UTF8.GetByteCount(body), ctx.Request.ContentLength);
                Assert.Equal("*", ctx.Request.Headers.IfNoneMatch);
                Assert.Equal("unchanged", ctx.Request.Headers["X-Input"]);
                using var reader = new StreamReader(ctx.Request.Body, leaveOpen: true);
                Assert.Equal(body, await reader.ReadToEndAsync());
                await ctx.Response.WriteAsync(ResponseJson);
            });
            await InvokeAsync(middleware, context, history: history, options: options, sinks: [sink]);
        }

        Assert.Equal(3, sink.Analyses.Select(x => x.RequestKey).Distinct().Count());
        Assert.All(sink.Analyses.Take(3), x => Assert.Equal(CacheRecommendation.FirstObservation, x.Recommendation));
        Assert.Equal(CacheRecommendation.CacheCandidate, sink.Analyses[3].Recommendation);
    }

    [Theory]
    [InlineData(200, "GET")]
    [InlineData(200, "QUERY")]
    [InlineData(202, "GET")]
    [InlineData(304, "GET")]
    [InlineData(400, "GET")]
    public async Task EndpointStatusHeadersAndBody_ArePreserved(int status, string method)
    {
        var context = CreateContext("*");
        context.Request.Method = method;
        var bytes = status == 304 ? Array.Empty<byte>() : new byte[] { 0, 255, 42, 128 };
        var middleware = new CacheAnalyzerMiddleware(async ctx =>
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.ContentLength = bytes.Length;
            ctx.Response.Headers.ETag = "\"endpoint-owned\"";
            ctx.Response.Headers.CacheControl = "private, max-age=60";
            ctx.Response.Headers["X-Endpoint"] = "preserved";
            await ctx.Response.Body.WriteAsync(bytes);
        });

        await InvokeAsync(middleware, context);

        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal("application/octet-stream", context.Response.ContentType);
        Assert.Equal((long)bytes.Length, context.Response.ContentLength);
        Assert.Equal("\"endpoint-owned\"", context.Response.Headers.ETag);
        Assert.Equal("private, max-age=60", context.Response.Headers.CacheControl);
        Assert.Equal("preserved", context.Response.Headers["X-Endpoint"]);
        Assert.Equal(bytes, ((MemoryStream)context.Response.Body).ToArray());
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
        ICacheAnalyzerRuntimeControl? runtimeControl = null,
        ILogger<CacheAnalyzerMiddleware>? logger = null)
    {
        var configuredOptions = options ?? new CacheAnalysisOptions();
        return middleware.InvokeAsync(
            context,
            provider ?? new Sha256ResponseFingerprintProvider(),
            history ?? new InMemoryResponseFingerprintHistory(),
            new StaticOptionsMonitor<CacheAnalysisOptions>(configuredOptions),
            runtimeControl ?? new CacheAnalyzerRuntimeControl(),
            sinks ?? [],
            logger ?? NullLogger<CacheAnalyzerMiddleware>.Instance);
    }

    private sealed class ThrowingFingerprintProvider : IResponseFingerprintProvider
    {
        public string CreateFingerprint(byte[] responseBody) => throw new InvalidOperationException("Bypassed requests must not be hashed.");
    }

    private sealed class CollectingLogger : ILogger<CacheAnalyzerMiddleware>
    {
        public System.Collections.Concurrent.ConcurrentQueue<LogLevel> Levels { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Levels.Enqueue(logLevel);
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
