using CacheAnalyzer.AspNetCore.Middleware;
using CacheAnalyzer.AspNetCore.Services.Runtime;
using CacheAnalyzer.Core.Models;
using CacheAnalyzer.Core.Options;
using CacheAnalyzer.Core.Services.Candidates;
using CacheAnalyzer.Core.Services.Fingerprinting;
using CacheAnalyzer.Core.Services.History;
using CacheAnalyzer.Core.Services.Reporting;
using CacheAnalyzerHelper.Attributes;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CacheAnalyzerHelper.Tests.AspNetCore.Middleware;

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
        var options = new CacheAnalysisOptions { MaximumDistinctRequests = 1 };
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
        options.MaximumDistinctRequests = 100;
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
        var options = new CacheAnalysisOptions { MaximumDistinctRequests = limit };
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
        var options = new CacheAnalysisOptions { MaximumDistinctRequests = 1 };
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
    public async Task SlowExactUrl_BypassesRepeatButAllowsOtherQueryVariants()
    {
        var options = new CacheAnalysisOptions { MaximumFingerprintDuration = TimeSpan.FromMilliseconds(1) };
        var history = new InMemoryResponseFingerprintHistory();
        var sink = new CollectingSink();
        var bypass = false;
        var middleware = new CacheAnalyzerMiddleware(async ctx =>
        {
            if (bypass) Assert.Same(ctx.Items["originalBody"], ctx.Response.Body);
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength = Encoding.UTF8.GetByteCount(ResponseJson);
            ctx.Response.StatusCode = 202;
            ctx.Response.Headers.ETag = "\"owned\"";
            await ctx.Response.WriteAsync(ResponseJson);
        });
        var first = CreateContext();
        first.Request.QueryString = new QueryString("?id=1");
        await InvokeAsync(middleware, first, new SlowFingerprintProvider(), history, options, [sink]);

        bypass = true;
        var repeat = CreateContext();
        repeat.Request.QueryString = first.Request.QueryString;
        repeat.Items["originalBody"] = repeat.Response.Body;
        await InvokeAsync(middleware, repeat, new ThrowingFingerprintProvider(), history, options, [sink]);
        Assert.Equal(ResponseJson, ReadBody(repeat));
        Assert.Equal(202, repeat.Response.StatusCode);
        Assert.Equal("application/json", repeat.Response.ContentType);
        Assert.Equal((long)Encoding.UTF8.GetByteCount(ResponseJson), repeat.Response.ContentLength);
        Assert.Equal("\"owned\"", repeat.Response.Headers.ETag);
        Assert.Equal(0, history.GetCountDistinctRequests());
        Assert.Single(sink.Analyses);

        bypass = false;
        options.MaximumFingerprintDuration = TimeSpan.MaxValue;
        var different = CreateContext();
        different.Request.QueryString = new QueryString("?id=2");
        await InvokeAsync(middleware, different, history: history, options: options, sinks: [sink]);
        Assert.Equal(2, sink.Analyses.Count);
        Assert.Equal(CacheRecommendation.FirstObservation, sink.Analyses[1].Recommendation);
    }

    [Fact]
    public async Task SlowQueryVariants_ExcludeSamePathOnlyAtThreshold()
    {
        var options = new CacheAnalysisOptions
        {
            MaximumFingerprintDuration = TimeSpan.FromMilliseconds(1),
            MaximumSlowFingerprintRequestsPerPath = 1
        };
        var sink = new CollectingSink();
        var middleware = CreateMiddleware();
        for (var i = 0; i < 2; i++)
        {
            var context = CreateContext();
            context.Request.Path = "/products";
            context.Request.QueryString = new QueryString("?id=" + i);
            await InvokeAsync(middleware, context, new SlowFingerprintProvider(), options: options, sinks: [sink]);
        }
        var excluded = CreateContext();
        excluded.Request.Path = "/products";
        excluded.Request.QueryString = new QueryString("?id=unseen");
        await InvokeAsync(middleware, excluded, new ThrowingFingerprintProvider(), options: options, sinks: [sink]);
        Assert.Equal(ResponseJson, ReadBody(excluded));
        Assert.Equal(2, sink.Analyses.Count);

        options.MaximumFingerprintDuration = TimeSpan.MaxValue;
        var otherPath = CreateContext();
        otherPath.Request.Path = "/products/123";
        await InvokeAsync(middleware, otherPath, options: options, sinks: [sink]);
        Assert.Equal(3, sink.Analyses.Count);
    }

    [Fact]
    public async Task TooManySlowVariants_ExcludeOnlyTheirPathAndPreserveHistory()
    {
        var history = new InMemoryResponseFingerprintHistory();
        var options = new CacheAnalysisOptions
        {
            MaximumFingerprintDuration = TimeSpan.FromMilliseconds(1),
            MaximumSlowFingerprintRequestsPerPath = 2
        };
        history.Observe("previous-fast", "A", 100, DateTimeOffset.UtcNow, options);
        var sink = new CollectingSink();
        var logger = new CollectingLogger();
        var middleware = CreateMiddleware();
        for (var i = 0; i < 3; i++)
        {
            var context = CreateContext();
            context.Request.Path = "/slow";
            context.Request.QueryString = new QueryString("?id=" + i);
            await InvokeAsync(middleware, context, new SlowFingerprintProvider(), history, options, [sink], logger: logger);
            Assert.Equal(ResponseJson, ReadBody(context));
        }
        var excluded = CreateContext();
        excluded.Request.Path = "/slow";
        excluded.Request.QueryString = new QueryString("?id=unseen");
        await InvokeAsync(middleware, excluded, new ThrowingFingerprintProvider(), history, options, [sink], logger: logger);
        Assert.Equal(ResponseJson, ReadBody(excluded));
        options.MaximumFingerprintDuration = TimeSpan.MaxValue;
        var other = CreateContext();
        other.Request.Path = "/other";
        await InvokeAsync(middleware, other, history: history, options: options, sinks: [sink], logger: logger);
        Assert.Equal(2, history.GetCountDistinctRequests());
        Assert.Equal(4, sink.Analyses.Count);
        Assert.Equal(CacheRecommendation.FirstObservation, sink.Analyses[3].Recommendation);
        Assert.Equal(LogLevel.Information, Assert.Single(logger.Levels));
        Assert.DoesNotContain("/slow", Assert.Single(logger.Messages));
        Assert.Contains("path ID", Assert.Single(logger.Messages));
    }

    [Fact]
    public async Task TooManySlowPaths_StopGloballyLogOnceAndClearHistory()
    {
        var history = new InMemoryResponseFingerprintHistory();
        var options = new CacheAnalysisOptions
        {
            MaximumFingerprintDuration = TimeSpan.FromMilliseconds(1),
            MaximumSlowFingerprintRequests = 2
        };
        history.Observe("previous-fast", "A", 100, DateTimeOffset.UtcNow, options);
        var sink = new CollectingSink();
        var logger = new CollectingLogger();
        var bypass = false;
        var middleware = new CacheAnalyzerMiddleware(async ctx =>
        {
            if (bypass) Assert.Same(ctx.Items["originalBody"], ctx.Response.Body);
            ctx.Response.StatusCode = 202;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength = Encoding.UTF8.GetByteCount(ResponseJson);
            ctx.Response.Headers.ETag = "\"owned\"";
            await ctx.Response.WriteAsync(ResponseJson);
        });
        for (var i = 0; i < 3; i++)
        {
            var context = CreateContext();
            context.Request.Path = "/slow/" + i;
            await InvokeAsync(middleware, context, new SlowFingerprintProvider(), history, options, [sink], logger: logger);
            Assert.Equal(ResponseJson, ReadBody(context));
            Assert.Equal(202, context.Response.StatusCode);
            Assert.Equal("\"owned\"", context.Response.Headers.ETag);
        }
        bypass = true;
        options.MaximumSlowFingerprintRequests = 100;
        for (var i = 0; i < 3; i++)
        {
            var context = CreateContext();
            context.Request.Path = "/later/" + i;
            context.Items["originalBody"] = context.Response.Body;
            await InvokeAsync(middleware, context, new ThrowingFingerprintProvider(), history, options, [sink], logger: logger);
            Assert.Equal(ResponseJson, ReadBody(context));
            Assert.Equal(202, context.Response.StatusCode);
            Assert.Equal("application/json", context.Response.ContentType);
            Assert.Equal((long)Encoding.UTF8.GetByteCount(ResponseJson), context.Response.ContentLength);
            Assert.Equal("\"owned\"", context.Response.Headers.ETag);
        }
        Assert.Equal(0, history.GetCountDistinctRequests());
        Assert.Equal(2, sink.Analyses.Count);
        Assert.Equal(LogLevel.Information, Assert.Single(logger.Levels));
        Assert.Contains("analysis stopped", Assert.Single(logger.Messages));
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OversizedResponse_PassesThroughDuringWrites(bool declaredLength, bool synchronous)
    {
        var context = CreateContext();
        var original = (MemoryStream)context.Response.Body;
        var sink = new CollectingSink();
        var history = new InMemoryResponseFingerprintHistory();
        var middleware = new CacheAnalyzerMiddleware(async ctx =>
        {
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.Headers.ETag = "\"original\"";
            if (declaredLength) ctx.Response.ContentLength = 9;
            for (var i = 0; i < 3; i++)
            {
                var chunk = new byte[] { (byte)(i * 3), (byte)(i * 3 + 1), (byte)(i * 3 + 2) };
                if (synchronous) ctx.Response.Body.Write(chunk, 0, chunk.Length);
                else await ctx.Response.Body.WriteAsync(chunk.AsMemory());
                Assert.Equal((i + 1) * 3L, original.Length);
            }
        });

        await InvokeAsync(middleware, context, new ThrowingFingerprintProvider(), history,
            new CacheAnalysisOptions { MaximumPayloadBytes = 5 }, [sink]);

        Assert.Same(original, context.Response.Body);
        Assert.Equal(Enumerable.Range(0, 9).Select(i => (byte)i).ToArray(), original.ToArray());
        Assert.Equal(declaredLength ? 9L : (long?)null, context.Response.ContentLength);
        Assert.Equal("application/octet-stream", context.Response.ContentType);
        Assert.Equal("\"original\"", context.Response.Headers.ETag);
        Assert.Equal(CacheRecommendation.PayloadTooLarge, Assert.Single(sink.Analyses).Recommendation);
        Assert.Equal(0, history.GetCountDistinctRequests());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public async Task MultipleWrites_WithinLimit_AreAnalyzedWithOrWithoutContentLength(int writeApi, bool declaredLength)
    {
        var context = CreateContext();
        var original = (MemoryStream)context.Response.Body;
        var sink = new CollectingSink();
        var history = new InMemoryResponseFingerprintHistory();
        var middleware = new CacheAnalyzerMiddleware(async ctx =>
        {
            ctx.Response.StatusCode = 202;
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.Headers.ETag = "\"original\"";
            if (declaredLength) ctx.Response.ContentLength = 9;
            for (var i = 0; i < 3; i++)
            {

                var chunk = new byte[] { (byte)(i * 3), (byte)(i * 3 + 1), (byte)(i * 3 + 2) };
                switch (writeApi)
                {
                    case 0: ctx.Response.Body.Write(chunk, 0, chunk.Length); break;
                    case 1: ctx.Response.Body.Write(chunk.AsSpan()); break;
                    case 2: await ctx.Response.Body.WriteAsync(chunk, 0, chunk.Length, CancellationToken.None); break;
                    case 3: await ctx.Response.Body.WriteAsync(chunk.AsMemory()); break;
                }
                Assert.Equal((i + 1) * 3L, original.Length);
            }
        });

        var provider = new Sha256ResponseFingerprintProvider();
        await InvokeAsync(middleware, context, provider, history,
            new CacheAnalysisOptions { MaximumPayloadBytes = 9, MaximumFingerprintDuration = TimeSpan.FromSeconds(10) },
            [sink]);

        Assert.Same(original, context.Response.Body);
        Assert.Equal(Enumerable.Range(0, 9).Select(i => (byte)i).ToArray(), original.ToArray());
        Assert.Equal(declaredLength ? 9L : (long?)null, context.Response.ContentLength);
        Assert.Equal(202, context.Response.StatusCode);
        Assert.Equal("application/octet-stream", context.Response.ContentType);
        Assert.Equal("\"original\"", context.Response.Headers.ETag);
        var analysis = Assert.Single(sink.Analyses);
        Assert.Equal(CacheRecommendation.FirstObservation, analysis.Recommendation);
        Assert.Equal(9, analysis.PayloadBytes);
        Assert.Equal(1, history.GetCountDistinctRequests());
        Assert.Equal(provider.CreateFingerprint(original.ToArray()), analysis.FingerprintResponse);
    }

    [Theory]
    [InlineData(0, 9)]
    [InlineData(1, 9)]
    [InlineData(2, 9)]
    [InlineData(0, 5)]
    [InlineData(1, 5)]
    [InlineData(2, 5)]
    public async Task Flushes_ForwardProgressAndRetainOnlyEligibleAnalysis(int writeApi, int limit)
    {
        var context = CreateContext();
        using var destination = new FlushTrackingStream();
        context.Response.Body = destination;
        var sink = new CollectingSink();
        var provider = new Sha256ResponseFingerprintProvider();
        var expected = Enumerable.Range(0, 9).Select(i => (byte)i).ToArray();
        var middleware = new CacheAnalyzerMiddleware(async ctx =>
        {
            for (var i = 0; i < 3; i++)
            {
                var chunk = expected.AsMemory(i * 3, 3);
                if (writeApi == 0)
                {
                    ctx.Response.Body.Write(chunk.Span);
                    ctx.Response.Body.Flush();
                }
                else if (writeApi == 1)
                {
                    await ctx.Response.Body.WriteAsync(chunk);
                    await ctx.Response.Body.FlushAsync();
                }
                else
                {
                    await ctx.Response.BodyWriter.WriteAsync(chunk);
                }
                Assert.Equal(expected.Take((i + 1) * 3).ToArray(), destination.ToArray());
                Assert.Equal((i + 1) * 3L, destination.FlushedLengths.Last());
                Assert.Empty(sink.Analyses);
            }
        });

        await InvokeAsync(middleware, context,
            limit < expected.Length ? new ThrowingFingerprintProvider() : provider,
            options: new CacheAnalysisOptions { MaximumPayloadBytes = limit, MaximumFingerprintDuration = TimeSpan.FromSeconds(10) },
            sinks: [sink]);

        Assert.Same(destination, context.Response.Body);
        Assert.Equal(expected, destination.ToArray()); // No final replay or duplication.
        var analysis = Assert.Single(sink.Analyses);
        Assert.Equal(9, analysis.PayloadBytes);
        Assert.Equal(limit < expected.Length ? CacheRecommendation.PayloadTooLarge : CacheRecommendation.FirstObservation,
            analysis.Recommendation);
        Assert.Equal(limit < expected.Length ? null : provider.CreateFingerprint(expected), analysis.FingerprintResponse);
    }

    private sealed class FlushTrackingStream : MemoryStream
    {
        public List<long> FlushedLengths { get; } = [];
        public override void Flush() => FlushedLengths.Add(Length);
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Flush();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ResponseExactlyAtLimit_IsStillAnalyzed()
    {
        var context = CreateContext();
        var sink = new CollectingSink();
        await InvokeAsync(CreateMiddleware(), context,
            options: new CacheAnalysisOptions { MaximumPayloadBytes = Encoding.UTF8.GetByteCount(ResponseJson) },
            sinks: [sink]);
        Assert.Equal(CacheRecommendation.FirstObservation, Assert.Single(sink.Analyses).Recommendation);
        Assert.Equal(ResponseJson, ReadBody(context));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("QUERY")]
    public async Task Candidate_BypassesLaterMatchingRequests_ButAnalyzesOtherKeys(string method)
    {
        var candidates = new CacheCandidateStore();
        var middleware = new CacheAnalyzerMiddleware(async ctx =>
        {
            if (ctx.Items.ContainsKey("expectBypass"))
                Assert.Same(ctx.Items["originalBody"], ctx.Response.Body);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(ResponseJson);
        }, candidates);
        var history = new InMemoryResponseFingerprintHistory();
        var options = new CacheAnalysisOptions
        {
            MinimumObservationsPerKey = 2, MinimumPayloadToFingerprintRatio = 0,
            MaximumFingerprintDuration = TimeSpan.FromSeconds(10)
        };
        var sink = new CollectingSink();
        for (var i = 0; i < 2; i++)
        {
            var context = CreateContext();
            context.Request.Method = method;
            await InvokeAsync(middleware, context, history: history, options: options, sinks: [sink]);
        }
        Assert.Equal(CacheRecommendation.CacheCandidate, sink.Analyses.Last().Recommendation);
        Assert.Single(candidates.GetSnapshot());
        var repeat = CreateContext();
        repeat.Request.Method = method;
        repeat.Items["expectBypass"] = true;
        repeat.Items["originalBody"] = repeat.Response.Body;
        await InvokeAsync(middleware, repeat, new ThrowingFingerprintProvider(), history, options, [sink]);
        Assert.Equal(ResponseJson, ReadBody(repeat));
        Assert.Equal(2, sink.Analyses.Count);
        var other = CreateContext();
        other.Request.Method = method;
        if (method == "GET") other.Request.QueryString = new QueryString("?id=2");
        else other.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("different query"));
        await InvokeAsync(middleware, other, history: history, options: options, sinks: [sink]);
        Assert.Equal(CacheRecommendation.FirstObservation, sink.Analyses.Last().Recommendation);
        Assert.Equal(3, sink.Analyses.Count);
    }

    [Fact]
    public async Task CandidateLimit_StopsOnlyWhenExceeded_PreservesFindingsAndBypassesAllLaterRequests()
    {
        var candidates = new CacheCandidateStore();
        var middleware = new CacheAnalyzerMiddleware(async ctx =>
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(ResponseJson);
        }, candidates);
        var history = new InMemoryResponseFingerprintHistory();
        var logger = new CollectingLogger();
        var options = new CacheAnalysisOptions
        {
            MaximumCacheCandidates = 1, MinimumObservationsPerKey = 2,
            MinimumPayloadToFingerprintRatio = 0, MaximumFingerprintDuration = TimeSpan.FromSeconds(10)
        };
        var sink = new CollectingSink();
        foreach (var path in new[] { "/one", "/two" })
        {
            for (var i = 0; i < 2; i++)
            {
                var context = CreateContext();
                context.Request.Path = path;
                await InvokeAsync(middleware, context, history: history, options: options, sinks: [sink], logger: logger);
            }
        }
        Assert.Equal(2, candidates.Count);
        Assert.Equal(4, sink.Analyses.Count);
        Assert.Equal(0, history.GetCountDistinctRequests());
        Assert.Single(logger.Messages, message => message.Contains("cache candidates"));
        options.MaximumCacheCandidates = 100;
        var later = CreateContext();
        later.Request.Path = "/three";
        await InvokeAsync(middleware, later, new ThrowingFingerprintProvider(), history, options, [sink], logger: logger);
        Assert.Equal(ResponseJson, ReadBody(later));
        Assert.Equal(4, sink.Analyses.Count);
        Assert.Equal(2, candidates.GetSnapshot().Length);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task FingerprintLogging_GlobalOrEndpointOptIn_IsCarriedToAnalysis(
        bool globalOptIn, bool endpointOptIn, bool expected)
    {
        var context = CreateContext();
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new CacheAnalyzeAttribute { IncludeFingerprintInLogs = endpointOptIn }), "test"));
        var sink = new CollectingSink();
        await InvokeAsync(CreateMiddleware(), context, options: new CacheAnalysisOptions
        {
            IncludeFingerprintInLogs = globalOptIn,
            MaximumFingerprintDuration = TimeSpan.FromSeconds(10)
        }, sinks: [sink]);

        var analysis = Assert.Single(sink.Analyses);
        Assert.Equal(expected, analysis.IncludeFingerprintInLogs);
        Assert.NotNull(analysis.FingerprintResponse); // Analysis still retains the fingerprint internally.
        Assert.Equal(ResponseJson, ReadBody(context));
    }

    [Fact]
    public async Task SlowFingerprint_EndpointOptIn_IsCarriedToAnalysis()
    {
        var context = CreateContext();
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new CacheAnalyzeAttribute { IncludeFingerprintInLogs = true }), "test"));
        var sink = new CollectingSink();
        await InvokeAsync(CreateMiddleware(), context, new SlowFingerprintProvider(), options: new CacheAnalysisOptions
        {
            MaximumFingerprintDuration = TimeSpan.Zero
        }, sinks: [sink]);
        var analysis = Assert.Single(sink.Analyses);
        Assert.Equal(CacheRecommendation.FingerprintTooSlow, analysis.Recommendation);
        Assert.True(analysis.IncludeFingerprintInLogs);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task FullRequestDisclosure_DefaultsToHashedIdentity_WithRouteLabel(
        bool globalOptIn, bool endpointOptIn, bool expected)
    {
        var context = CreateContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("private.example");
        context.Request.Path = "/products/private-id";
        context.Request.QueryString = new QueryString("?token=secret");
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask,
            RoutePatternFactory.Parse("/products/{id}"), 0,
            new EndpointMetadataCollection(new CacheAnalyzeAttribute { IncludeFullRequestKeyInLogs = endpointOptIn }), "test"));
        var history = new RecordingHistory();
        var candidates = new CacheCandidateStore();
        var middleware = new CacheAnalyzerMiddleware(async ctx =>
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(ResponseJson);
        }, candidates);
        var sink = new CollectingSink();
        await InvokeAsync(middleware, context, history: history, options: new CacheAnalysisOptions
        {
            IncludeFullRequestKeyInLogs = globalOptIn,
            MaximumFingerprintDuration = TimeSpan.FromSeconds(10)
        }, sinks: [sink]);
        const string fullKey = "GET https://private.example/products/private-id?token=secret";
        var analysis = Assert.Single(sink.Analyses);
        Assert.Equal(RequestIdentity.Create(fullKey), analysis.FingerprintRequest);
        Assert.Equal(analysis.FingerprintRequest, history.Key);
        Assert.Equal("GET /products/{id}", analysis.Endpoint);
        Assert.Equal(expected, analysis.IncludeFullRequestKeyInLogs);
        Assert.Equal(expected ? fullKey : null, analysis.FullRequestKey);
        Assert.Equal(analysis.FingerprintRequest, Assert.Single(candidates.GetSnapshot()).RequestKey);
        Assert.Equal(ResponseJson, ReadBody(context));
    }

    private sealed class RecordingHistory : IResponseFingerprintHistory
    {
        public string? Key { get; private set; }
        public int GetCountDistinctRequests() => 0;
        public void Clear() { }
        public FingerprintHistoryResult Observe(string requestKey, string fingerprint, long payloadBytes,
            DateTimeOffset observedAt, CacheAnalysisOptions options)
        {
            Key = requestKey;
            return new FingerprintHistoryResult(CacheRecommendation.CacheCandidate);
        }
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
        public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Levels.Enqueue(logLevel);
            Messages.Enqueue(formatter(state, exception));
        }
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
