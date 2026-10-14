using CacheAnalyzer.AspNetCore.IO;
using CacheAnalyzer.AspNetCore.Services.Runtime;
using CacheAnalyzer.Core.Models;
using CacheAnalyzer.Core.Options;
using CacheAnalyzer.Core.Services.Candidates;
using CacheAnalyzer.Core.Services.Exclusions;
using CacheAnalyzer.Core.Services.Fingerprinting;
using CacheAnalyzer.Core.Services.History;
using CacheAnalyzer.Core.Services.Reporting;
using CacheAnalyzerHelper.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Security.Cryptography;

namespace CacheAnalyzer.AspNetCore.Middleware;

internal sealed class CacheAnalyzerMiddleware
{
    private readonly RequestDelegate _next;
    private readonly CacheCandidateStore _cacheableCandidates;
    private readonly SlowFingerprintExclusions _slowFingerprints = new();
    // Volatile reads provide cross-thread visibility of shutdown without locking.
    // In-flight analysis remains best effort; the atomic transition selects one logger.
    private int _analysisStopped;
    // 0 = not started, 1 = active, 2 = disabled. Transitions are advisory snapshots.
    private int _lifecycleState;

    public CacheAnalyzerMiddleware(RequestDelegate next, CacheCandidateStore? candidates = null)
    {
        _next = next;
        _cacheableCandidates = candidates ?? new CacheCandidateStore();
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
        #region before response - negative scenarios where analysis is stopped or cannot be analysed
        if (IsAnalysisStopped())
        {
            await _next(context);
            return;
        }

        var options = optionsMonitor.CurrentValue;
        if (!options.Enabled || !runtimeControl.IsEnabled)
        {
            if (Interlocked.CompareExchange(ref _lifecycleState, 2, 1) == 1)
            {
                logger.LogInformation(
                    "Cache analysis stopped because configuration or runtime control disabled it. Retained state was preserved; analysis can resume when enabled.");
            }
            await _next(context);
            return;
        }
        if (!ShouldAnalyze(context, options))
        {
            await _next(context);
            return;
        }

        if (StopAtCandidateCapacity(history, options, logger) || StopAtCapacity(history, options, logger))
        {
            await _next(context);
            return;
        }


        var requestKey = await CreateRequestKeyAsync(context.Request);
        var requestId = RequestIdentity.Create(requestKey);
        var routeTemplate = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        var endpoint = $"{context.Request.Method} {(routeTemplate is null ? "(unmapped endpoint)" : "/" + routeTemplate.TrimStart('/'))}";
        if (_cacheableCandidates.Contains(requestId))
        {
            await _next(context);
            return;
        }
        // Group only GET query variants; QUERY request bodies retain their own identity.
        var getPathKey = HttpMethods.IsGet(context.Request.Method)
            ? RequestIdentity.Create($"GET {context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}{context.Request.Path}")
            : null;
        if (_slowFingerprints.IsExcluded(requestId, getPathKey, options))
        {
            await _next(context);
            return;
        }
        #endregion

        var previousState = Interlocked.Exchange(ref _lifecycleState, 1);
        if (previousState == 0)
        {
            logger.LogInformation("Cache analysis started on the first eligible request for this pipeline.");
        }
        else if (previousState == 2)
        {
            logger.LogInformation("Cache analysis resumed after configuration and runtime control enabled it.");
        }

        var originalBody = context.Response.Body;
        var includeFingerprintInLogs = options.IncludeFingerprintInLogs ||
            context.GetEndpoint()?.Metadata.GetMetadata<CacheAnalyzeAttribute>()?.IncludeFingerprintInLogs == true;
        var includeFullRequestKeyInLogs = options.IncludeFullRequestKeyInLogs ||
            context.GetEndpoint()?.Metadata.GetMetadata<CacheAnalyzeAttribute>()?.IncludeFullRequestKeyInLogs == true;

        await using var bufferedBody = new LimitedResponseBuffer(originalBody, context.Response, options.MaximumPayloadBytes);
        context.Response.Body = bufferedBody;

        try
        {
            await _next(context);

            #region after response payload - negative scenario
            if (IsAnalysisStopped() || !CanAnalyzeResponse(context.Response))
            {
                return;
            }

            if (bufferedBody.ExceededLimit)
            {
                Report(
                    sinks,
                    logger,
                    new ResponseAnalysis(
                        requestKey,
                        CacheRecommendation.PayloadTooLarge,
                        bufferedBody.BytesWritten,
                        TimeSpan.Zero,
                        null,
                        includeFingerprintInLogs: includeFingerprintInLogs,
                        endpoint: endpoint,
                        includeFullRequestKeyInLogs: includeFullRequestKeyInLogs));
                return;
            }

            var responseBytes = bufferedBody.ToArray();
            var stopwatch = Stopwatch.StartNew();
            var fingerprint = fingerprintProvider.CreateFingerprint(responseBytes);
            stopwatch.Stop();

            if (stopwatch.Elapsed > options.MaximumFingerprintDuration)
            {
                var slowResult = _slowFingerprints.RecordSlowRequest(requestId, getPathKey, options);
                if (slowResult == SlowFingerprintRecordResult.CapacityExceeded)
                {
                    if (Interlocked.Exchange(ref _analysisStopped, 1) == 0)
                    {
                        ClearAnalysis(history);
                        logger.LogInformation(
                            "Cache analysis stopped after exceeding {MaximumSlowFingerprintRequests} slow request groups (GET paths or exact QUERY requests). Retained state was cleared and subsequent requests bypass analysis for this pipeline's lifetime.",
                            options.MaximumSlowFingerprintRequests);
                    }
                    return;
                }
                if (slowResult == SlowFingerprintRecordResult.PathExcluded)
                {
                    logger.LogInformation(
                        "Cache analysis excluded {Endpoint} (path ID {PathId}) after exceeding {MaximumSlowFingerprintRequestsPerPath} distinct slow fingerprint query variants. Other paths continue analysis.",
                        endpoint, getPathKey, options.MaximumSlowFingerprintRequestsPerPath);
                }
                Report(
                    sinks,
                    logger,
                    new ResponseAnalysis(
                        requestKey,
                        CacheRecommendation.FingerprintTooSlow,
                        responseBytes.LongLength,
                        stopwatch.Elapsed,
                        fingerprint,
                        includeFingerprintInLogs: includeFingerprintInLogs,
                        endpoint: endpoint,
                        includeFullRequestKeyInLogs: includeFullRequestKeyInLogs));
                return;
            }
            #endregion

            // Once hashing has started, let this request finish analysis and reporting.
            // Shutdown may overlap; the finally block releases any late observations.
            var historyResult = history.Observe(
                requestId,
                fingerprint,
                responseBytes.LongLength,
                DateTimeOffset.UtcNow,
                options);
            var analysis = new ResponseAnalysis(
                    requestKey,
                    historyResult.Recommendation,
                    responseBytes.LongLength,
                    stopwatch.Elapsed,
                    fingerprint,
                    includeFingerprintInLogs: includeFingerprintInLogs,
                    endpoint: endpoint,
                    includeFullRequestKeyInLogs: includeFullRequestKeyInLogs);
            // Other requests may already be analyzing this key. Preserve and report
            // only its first candidate finding; no requests wait for one another.
            if (analysis.Recommendation != CacheRecommendation.CacheCandidate ||
                (!IsAnalysisStopped() && _cacheableCandidates.TryAdd(analysis)))
            {
                Report(sinks, logger, analysis);
            }

            if (!StopAtCandidateCapacity(history, options, logger))
                StopAtCapacity(history, options, logger);
        }
        finally
        {
            context.Response.Body = originalBody;
            // A request may have entered Observe concurrently with shutdown.
            if (IsAnalysisStopped()) ClearAnalysis(history);
        }
    }

    private bool StopAtCandidateCapacity(
        IResponseFingerprintHistory history, CacheAnalysisOptions options, ILogger logger)
    {
        if (_cacheableCandidates.Count <= options.MaximumCacheCandidates) return false;
        if (Interlocked.Exchange(ref _analysisStopped, 1) == 0)
        {
            ClearAnalysis(history);
            logger.LogInformation(
                "Cache analysis stopped after exceeding {MaximumCacheCandidates} cache candidates. Candidate findings were preserved; subsequent requests bypass analysis for this pipeline's lifetime. Rerun analysis after relevant data or application changes.",
                options.MaximumCacheCandidates);
        }
        return true;
    }

    private bool StopAtCapacity(
        IResponseFingerprintHistory history,
        CacheAnalysisOptions options,
        ILogger logger)
    {
        if (history.GetCountDistinctRequests() < options.MaximumDistinctRequests) return false;

        if (Interlocked.Exchange(ref _analysisStopped, 1) == 0)
        {
            ClearAnalysis(history);
            logger.LogInformation(
                "Cache analysis stopped after reaching the distinct request limit of {MaximumDistinctRequests}. Retained history was cleared; subsequent requests bypass analysis for this pipeline's lifetime.",
                options.MaximumDistinctRequests);
        }
        return true;
    }

    private bool IsAnalysisStopped()
    {
        return Volatile.Read(ref _analysisStopped) != 0;
    }

    private void ClearAnalysis(IResponseFingerprintHistory history)
    {
        history.Clear();
        _slowFingerprints.Clear();
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
                logger.LogError(exception, "A cache analysis sink failed for {Endpoint} (fingerprintRequest={fingerprintRequest})", analysis.Endpoint, analysis.FingerprintRequest);
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
        return response.StatusCode == StatusCodes.Status200OK &&
               !response.Headers.CacheControl.Any(
                   value => value?.Contains("no-store", StringComparison.OrdinalIgnoreCase) == true);
    }

}
