using System.Text;
using CacheAnalyzer.AspNetCore.Middleware;
using CacheAnalyzer.AspNetCore.Services;
using CacheAnalyzer.Core.Models;
using CacheAnalyzer.Core.Options;
using CacheAnalyzer.Core.Services.Candidates;
using CacheAnalyzer.Core.Services.History;
using CacheAnalyzerHelper.Attributes;
using Microsoft.AspNetCore.Http;

namespace CacheAnalyzerHelper.Tests.AspNetCore.Middleware;

public sealed partial class CacheAnalyzerMiddlewareTests
{
    [Theory]
    [InlineData(true, CacheRecommendation.StableDataCandidate)]
    [InlineData(false, CacheRecommendation.ResponseChanged)]
    public async Task VolatileIds_AreIgnoredOnlyWhenConfigured_AndHttpResponseIsPreserved(
        bool exclude, CacheRecommendation expected)
    {
        var history = new InMemoryResponseFingerprintHistory();
        var sink = new CollectingSink();
        var candidates = new CacheCandidateStore();
        var body = "";
        var middleware = new CacheAnalyzerMiddleware(async ctx =>
        {
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength = Encoding.UTF8.GetByteCount(body);
            ctx.Response.Headers.ETag = "\"owned-by-app\"";
            ctx.Response.Headers["X-App"] = "preserved";
            await ctx.Response.WriteAsync(body);
        }, candidates);
        foreach (var id in new[] { "aaa", "bbb" })
        {
            body = "{\"request_id\":\"" + id + "\",\"results\":[{\"title\":\"same\"}]}";
            var context = CreateContext();
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
                new EndpointMetadataCollection(new CacheAnalyzeAttribute
                { IgnoreJsonPaths = exclude ? ["/request_id"] : [] }), "news"));
            await InvokeAsync(middleware, context, history: history, sinks: [sink], options: new CacheAnalysisOptions
            {
                MinimumObservationsPerKey = 2, MinimumPayloadToFingerprintRatio = 0,
                MaximumFingerprintDuration = TimeSpan.MaxValue
            });
            Assert.Equal(body, ReadBody(context));
            Assert.Equal(200, context.Response.StatusCode);
            Assert.Equal(Encoding.UTF8.GetByteCount(body), context.Response.ContentLength);
            Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);
            Assert.Equal("\"owned-by-app\"", context.Response.Headers.ETag);
            Assert.Equal("preserved", context.Response.Headers["X-App"]);
        }
        Assert.Equal(expected, sink.Analyses.Last().Recommendation);
        Assert.Equal(exclude ? "JsonExclusionsApplied" : "ExactBytes", sink.Analyses.Last().FingerprintComparison);
        Assert.Equal(exclude ? 1 : 0, candidates.Count);
    }

    [Theory]
    [InlineData("{\"request_id\":1,\"b\":2,\"a\":1}", "{ \"a\":1, \"b\":2, \"request_id\":2 }", "/request_id", true)]
    [InlineData("{\"nested\":{\"id\":1},\"id\":5}", "{\"nested\":{\"id\":2},\"id\":5}", "/nested/id", true)]
    [InlineData("{\"nested\":{\"id\":1},\"id\":5}", "{\"nested\":{\"id\":2},\"id\":5}", "/id", false)]
    [InlineData("{\"a/b\":{\"~id\":1}}", "{\"a/b\":{\"~id\":2}}", "/a~1b/~0id", true)]
    [InlineData("{\"items\":[{\"id\":1},{\"id\":3}]}", "{\"items\":[{\"id\":2},{\"id\":3}]}", "/items/0/id", true)]
    [InlineData("[1,2]", "[2,1]", "/missing", false)]
    [InlineData("{\"title\":\"old\",\"request_id\":1}", "{\"title\":\"new\",\"request_id\":2}", "/request_id", false)]
    [InlineData("{\"x\":null}", "{}", "/missing", false)]
    public void JsonPaths_NormalizeOnlySelectedParts(string first, string second, string pointer, bool equal)
    {
        var context = CreateContext();
        context.Response.ContentType = "application/problem+json";
        Assert.True(JsonFingerprintInput.TryNormalize(Encoding.UTF8.GetBytes(first), context.Response, [pointer], out var left));
        Assert.True(JsonFingerprintInput.TryNormalize(Encoding.UTF8.GetBytes(second), context.Response, [pointer], out var right));
        Assert.Equal(equal, left.SequenceEqual(right));
    }

    [Fact]
    public void MultiplePaths_OmitOnlyConfiguredSubtrees_UsingOriginalArrayIndices()
    {
        var context = CreateContext();
        context.Response.ContentType = "application/json";
        Assert.True(JsonFingerprintInput.TryNormalize(
            Encoding.UTF8.GetBytes("{\"request_id\":1,\"items\":[0,1,2,3],\"meta\":{\"a\":1},\"keep\":true}"),
            context.Response, ["/request_id", "/items/0", "/items/2", "/meta", "/missing"], out var normalized));
        Assert.Equal("{\"items\":[1,3],\"keep\":true}", Encoding.UTF8.GetString(normalized));
    }

    [Fact]
    public void DeepJson_FallsBackWithoutChangingInput()
    {
        var context = CreateContext();
        context.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(new string('[', 65) + "0" + new string(']', 65));
        Assert.False(JsonFingerprintInput.TryNormalize(bytes, context.Response, ["/missing"], out var input));
        Assert.Same(bytes, input);
    }

    [Fact]
    public async Task AppliedAndFallbackObservations_DoNotCountAsIdenticalEvenWhenHashInputMatches()
    {
        var history = new InMemoryResponseFingerprintHistory();
        var sink = new CollectingSink();
        var contentType = "application/json";
        var middleware = new CacheAnalyzerMiddleware(async ctx =>
        {
            ctx.Response.ContentType = contentType;
            await ctx.Response.WriteAsync("{}");
        });
        foreach (var type in new[] { "application/json", "text/plain" })
        {
            contentType = type;
            var context = CreateContext();
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
                new EndpointMetadataCollection(new CacheAnalyzeAttribute { IgnoreJsonPaths = ["/missing"] }), "test"));
            await InvokeAsync(middleware, context, history: history, sinks: [sink], options: new CacheAnalysisOptions
            {
                MinimumObservationsPerKey = 2, MinimumPayloadToFingerprintRatio = 0,
                MaximumFingerprintDuration = TimeSpan.MaxValue
            });
        }
        Assert.Equal(sink.Analyses[0].FingerprintResponse, sink.Analyses[1].FingerprintResponse);
        Assert.Equal(CacheRecommendation.ResponseChanged, sink.Analyses[1].Recommendation);
        Assert.Equal("JsonExclusionsNotApplied", sink.Analyses[1].FingerprintComparison);
    }

    [Theory]
    [InlineData("text/plain", "{\"id\":1}", "/id", "")]
    [InlineData("application/json", "{broken", "/id", "")]
    [InlineData("application/json; charset=utf-16", "{}", "/id", "")]
    [InlineData("application/json", "{}", "/id", "gzip")]
    [InlineData("application/json", "{\"id\":1,\"id\":2}", "/id", "")]
    [InlineData("application/json", "{}", "id", "")]
    [InlineData("application/json", "{}", "", "")]
    [InlineData("application/json", "{}", "/~2", "")]
    [InlineData("application/json", "{\"\\uDEAD\":1}", "/id", "")]
    public async Task UnsupportedPayloads_FallBackToExactBytes_AndReportFallback(
        string contentType, string body, string pointer, string encoding)
    {
        var sink = new CollectingSink();
        var context = CreateContext();
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new CacheAnalyzeAttribute { IgnoreJsonPaths = [pointer] }), "test"));
        var middleware = new CacheAnalyzerMiddleware(async ctx =>
        {
            ctx.Response.ContentType = contentType;
            if (encoding.Length > 0) ctx.Response.Headers.ContentEncoding = encoding;
            await ctx.Response.WriteAsync(body);
        });
        await InvokeAsync(middleware, context, sinks: [sink], options: new CacheAnalysisOptions
        { MaximumFingerprintDuration = TimeSpan.MaxValue });
        var analysis = Assert.Single(sink.Analyses);
        Assert.Equal("JsonExclusionsNotApplied", analysis.FingerprintComparison);
        Assert.Equal(new CacheAnalyzer.Core.Services.Fingerprinting.Sha256ResponseFingerprintProvider()
            .CreateFingerprint(Encoding.UTF8.GetBytes(body)), analysis.FingerprintResponse);
        Assert.Equal(body, ReadBody(context));
    }
}
