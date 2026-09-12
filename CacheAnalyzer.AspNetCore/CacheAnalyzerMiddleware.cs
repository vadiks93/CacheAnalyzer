using CacheAnalyzer.Core;
using CacheAnalyzerHelper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Security.Cryptography;

namespace CacheAnalyzer.AspNetCore;

public sealed class CacheAnalyzerMiddleware
{
    private readonly RequestDelegate _next;
    // Volatile.Read is intentionally omitted: delayed visibility and a few extra or
    // missed observations are acceptable. Only the one-time log needs an atomic transition.
    private int _capacityReached;

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
        if (_capacityReached != 0)
        {
            await _next(context);
            return;
        }

        var options = optionsMonitor.CurrentValue;
        if (!options.Enabled || !runtimeControl.IsEnabled || !ShouldAnalyze(context, options))
        {
            await _next(context);
            return;
        }

        if (StopAtCapacity(history, options, logger))
        {
            await _next(context);
            return;
        }

        var requestKey = await CreateRequestKeyAsync(context.Request);
        var originalBody = context.Response.Body;

        await using var bufferedBody = new MemoryStream();
        context.Response.Body = bufferedBody;

        try
        {
            await _next(context);

            if (StopAtCapacity(history, options, logger) || !CanAnalyzeResponse(context.Response))
            {
                await CopyResponseAsync(bufferedBody, originalBody);
                return;
            }

            var responseBytes = bufferedBody.ToArray();

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
            var fingerprint = fingerprintProvider.CreateFingerprint(responseBytes);
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
                        fingerprint));
                await originalBody.WriteAsync(responseBytes, context.RequestAborted);
                return;
            }

            if (StopAtCapacity(history, options, logger))
            {
                await originalBody.WriteAsync(responseBytes, context.RequestAborted);
                return;
            }

            var historyResult = history.Observe(
                requestKey,
                fingerprint,
                responseBytes.LongLength,
                DateTimeOffset.UtcNow,
                options);
            if (_capacityReached == 0)
            {
                Report(
                    sinks,
                    logger,
                    new ResponseAnalysis(
                        requestKey,
                        historyResult.Recommendation,
                        responseBytes.LongLength,
                        stopwatch.Elapsed,
                        fingerprint));
            }

            StopAtCapacity(history, options, logger);

            await originalBody.WriteAsync(responseBytes, context.RequestAborted);
        }
        finally
        {
            context.Response.Body = originalBody;
            // A request may have entered Observe concurrently with shutdown.
            if (_capacityReached != 0) history.Clear();
        }
    }

    private bool StopAtCapacity(
        IResponseFingerprintHistory history,
        CacheAnalysisOptions options,
        ILogger logger)
    {
        if (_capacityReached != 0) return true;
        if (history.GetCountDistinctRequests() < options.MaximumDistinctRequestKeys) return false;

        if (Interlocked.Exchange(ref _capacityReached, 1) == 0)
        {
            history.Clear();
            logger.LogInformation(
                "Cache analysis stopped after reaching the distinct request key limit of {MaximumDistinctRequestKeys}. Retained history was cleared; subsequent requests bypass analysis for this pipeline's lifetime.",
                options.MaximumDistinctRequestKeys);
        }

        return true;
    }

    private static async Task<string> CreateRequestKeyAsync(HttpRequest request)
    {
        var key = $"{request.Method} {request.Scheme}://{request.Host}{request.PathBase}{request.Path}{request.QueryString}";
        if (!IsQuery(request.Method))
        {
            return key;
        }

        request.EnableBuffering();
        var position = request.Body.Position;
        try
        {
            var hash = await SHA256.HashDataAsync(request.Body, request.HttpContext.RequestAborted);
            return key + " QUERY " + System.Text.Json.JsonSerializer.Serialize(new[]
            {
                request.ContentType,
                request.Headers.ContentEncoding.ToString(),
                request.Headers.ContentLanguage.ToString(),
                Convert.ToHexString(hash)
            });
        }
        finally
        {
            request.Body.Position = position;
        }
    }

    private static bool IsQuery(string method) => string.Equals(method, "QUERY", StringComparison.Ordinal);

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
        var isCacheableMethod = HttpMethods.IsGet(method) || IsQuery(method);

        if (!isCacheableMethod)
        {
            return false;
        }

        return options.EndpointSelection == EndpointSelectionMode.AllGetAndQueryRequests ||
               context.GetEndpoint()?.Metadata.GetMetadata<CacheAnalyzeAttribute>() is not null;
    }

    private static bool CanAnalyzeResponse(HttpResponse response)
    {
        return response.StatusCode >= StatusCodes.Status200OK &&
               response.StatusCode < StatusCodes.Status300MultipleChoices &&
               !response.Headers.CacheControl.Any(
                   value => value?.Contains("no-store", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static async Task CopyResponseAsync(Stream source, Stream destination)
    {
        source.Position = 0;
        await source.CopyToAsync(destination);
    }
}
