using CacheAnalyzer.Core;
using CacheAnalyzerHelper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace CacheAnalyzer.AspNetCore;

public sealed class CacheAnalyzerMiddleware
{
    private readonly RequestDelegate _next;

    public CacheAnalyzerMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IResponseFingerprintProvider fingerprintProvider,
        IResponseFingerprintHistory history,
        IOptionsMonitor<CacheAnalysisOptions> optionsMonitor,
        ICacheAnalyzerRuntimeControl runtimeControl,
        IEnumerable<IResponseAnalysisSink> sinks,
        ILogger<CacheAnalyzerMiddleware> logger)
    {
        var options = optionsMonitor.CurrentValue;
        if (!options.Enabled || !runtimeControl.IsEnabled || !ShouldAnalyze(context, options))
        {
            await _next(context);
            return;
        }

        var originalBody = context.Response.Body;

        await using var bufferedBody = new MemoryStream();
        context.Response.Body = bufferedBody;

        try
        {
            await _next(context);

            if (!CanReturnCachedResult(context.Response))
            {
                await CopyResponseAsync(bufferedBody, originalBody);
                return;
            }

            var responseBytes = bufferedBody.ToArray();
            var requestKey = CreateRequestKey(context.Request);

            if (responseBytes.LongLength > options.MaximumPayloadBytes)
            {
                Report(
                    sinks,
                    logger,
                    new ResponseAnalysis(
                        requestKey,
                        CacheRecommendation.PayloadTooLarge,
                        responseBytes.LongLength,
                        TimeSpan.Zero,
                        null));
                await originalBody.WriteAsync(responseBytes, context.RequestAborted);
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            var etag = fingerprintProvider.CreateFingerprint(responseBytes);
            stopwatch.Stop();

            if (stopwatch.Elapsed > options.MaximumFingerprintDuration)
            {
                Report(
                    sinks,
                    logger,
                    new ResponseAnalysis(
                        requestKey,
                        CacheRecommendation.FingerprintTooSlow,
                        responseBytes.LongLength,
                        stopwatch.Elapsed,
                        etag));
                await originalBody.WriteAsync(responseBytes, context.RequestAborted);
                return;
            }

            var historyResult = history.Observe(
                requestKey,
                etag,
                responseBytes.LongLength,
                DateTimeOffset.UtcNow,
                options);
            Report(
                sinks,
                logger,
                new ResponseAnalysis(
                    requestKey,
                    historyResult.Recommendation,
                    responseBytes.LongLength,
                    stopwatch.Elapsed,
                    etag,
                    historyResult.TrackingCapacityReached));

            context.Response.Headers.ETag = etag;

            if (Matches(context.Request.Headers.IfNoneMatch, etag))
            {
                context.Response.StatusCode = StatusCodes.Status304NotModified;
                context.Response.ContentLength = null;
                context.Response.Headers.Remove("Content-Type");
                return;
            }

            context.Response.ContentLength = responseBytes.Length;
            await originalBody.WriteAsync(responseBytes, context.RequestAborted);
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    private static string CreateRequestKey(HttpRequest request)
    {
        return $"{request.Method} {request.Scheme}://{request.Host}{request.PathBase}{request.Path}{request.QueryString}";
    }

    private static void Report(
        IEnumerable<IResponseAnalysisSink> sinks,
        ILogger logger,
        ResponseAnalysis analysis)
    {
        foreach (var sink in sinks)
        {
            try
            {
                sink.Report(analysis);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "A cache analysis sink failed for {RequestKey}", analysis.RequestKey);
            }
        }
    }

    private static bool ShouldAnalyze(HttpContext context, CacheAnalysisOptions options)
    {
        var method = context.Request.Method;
        var isCacheableMethod = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);

        if (!isCacheableMethod)
        {
            return false;
        }

        return options.EndpointSelection == EndpointSelectionMode.AllGetAndHeadRequests ||
               context.GetEndpoint()?.Metadata.GetMetadata<CacheAnalyzeAttribute>() is not null;
    }

    private static bool CanReturnCachedResult(HttpResponse response)
    {
        return response.StatusCode >= StatusCodes.Status200OK &&
               response.StatusCode < StatusCodes.Status300MultipleChoices &&
               !response.Headers.CacheControl.Any(
                   value => value?.Contains("no-store", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static bool Matches(StringValues candidates, string etag)
    {
        foreach (var header in candidates)
        {
            foreach (var candidate in header?.Split(',') ?? Array.Empty<string>())
            {
                var value = candidate.Trim();
                if (value == "*" || string.Equals(value, etag, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static async Task CopyResponseAsync(Stream source, Stream destination)
    {
        source.Position = 0;
        await source.CopyToAsync(destination);
    }
}
