using CacheAnalyzer.Core.Models;
using CacheAnalyzer.Core.Options;
using CacheAnalyzer.Core.Services.Candidates;
using CacheAnalyzer.Core.Services.Exclusions;
using CacheAnalyzer.Core.Services.Fingerprinting;
using CacheAnalyzer.Core.Services.History;
using System;
using System.Diagnostics;


using System.Net.Http;

using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace CacheAnalyzer.Sample.NetFramework.Handlers
{
    // Sample adapter for these small demo responses; not a general Web API integration.
    public sealed class SampleCacheAnalysisHandler : DelegatingHandler
    {
        private readonly IResponseFingerprintProvider fingerprints = new Sha256ResponseFingerprintProvider();
        private readonly IResponseFingerprintHistory history = new InMemoryResponseFingerprintHistory();
        private readonly SlowFingerprintExclusions slowFingerprints = new SlowFingerprintExclusions();
        public CacheCandidateStore Candidates { get; } = new CacheCandidateStore();
        // Volatile reads provide cross-thread visibility of shutdown without locking.
        // In-flight analysis remains best effort; the atomic transition selects one logger.
        private int analysisStopped;
        private readonly CacheAnalysisOptions options = new CacheAnalysisOptions
        {
            MinimumObservationsPerKey = 3,
            MinimumPayloadToFingerprintRatio = 10,
            MaximumDistinctRequests = 200,
            MaximumObservationsPerKey = 1_000
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref analysisStopped) != 0)
                return await base.SendAsync(request, cancellationToken);

            var isQuery = request.Method.Method == "QUERY";
            if ((request.Method != HttpMethod.Get && !isQuery) ||
                !request.RequestUri.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal))
                return await base.SendAsync(request, cancellationToken);

            if (StopAtCandidateCapacity() || StopAtCapacity()) return await base.SendAsync(request, cancellationToken);

            try
            {
                var key = request.Method + " " + request.RequestUri;
                if (isQuery)
                {
                    var body = request.Content == null ? new byte[0] : await request.Content.ReadAsByteArrayAsync();
                    using (var sha = SHA256.Create())
                        key += " QUERY " + JsonConvert.SerializeObject(new[]
                        {
                            request.Content?.Headers.ContentType?.ToString(),
                            request.Content == null ? "" : string.Join(",", request.Content.Headers.ContentEncoding),
                            request.Content == null ? "" : string.Join(",", request.Content.Headers.ContentLanguage),
                            Convert.ToBase64String(sha.ComputeHash(body))
                        });
                }

                var requestId = RequestIdentity.Create(key);
                var displayKey = options.IncludeFullRequestKeyInLogs ? key : requestId;
                var getPathKey = isQuery ? null : RequestIdentity.Create("GET " + request.RequestUri.GetLeftPart(UriPartial.Path));
                if (Candidates.Contains(requestId) || slowFingerprints.IsExcluded(requestId, getPathKey, options))
                    return await base.SendAsync(request, cancellationToken);

                var response = await base.SendAsync(request, cancellationToken);
                if (Volatile.Read(ref analysisStopped) != 0 ||
                    !response.IsSuccessStatusCode || response.Content == null || response.Headers.CacheControl?.NoStore == true)
                    return response;

                var bytes = await response.Content.ReadAsByteArrayAsync();
                if (bytes.LongLength > options.MaximumPayloadBytes)
                {
                    Console.WriteLine("{0}: {1}, {2} bytes", displayKey, CacheRecommendation.PayloadTooLarge, bytes.Length);
                    return response;
                }
                var watch = Stopwatch.StartNew();
                var fingerprint = fingerprints.CreateFingerprint(bytes);
                watch.Stop();
                if (watch.Elapsed > options.MaximumFingerprintDuration)
                {
                    var slowResult = slowFingerprints.RecordSlowRequest(requestId, getPathKey, options);
                    if (slowResult == SlowFingerprintRecordResult.CapacityExceeded)
                    {
                        if (Interlocked.Exchange(ref analysisStopped, 1) == 0)
                        {
                            ClearAnalysis();
                            Console.WriteLine("Cache analysis stopped after exceeding {0} slow request groups (GET paths or exact QUERY requests). Retained state cleared; subsequent requests bypass analysis for this pipeline's lifetime.", options.MaximumSlowFingerprintRequests);
                        }
                        return response;
                    }
                    if (slowResult == SlowFingerprintRecordResult.PathExcluded)
                    {
                        Console.WriteLine("Cache analysis excluded {0} after exceeding {1} distinct slow fingerprint query variants. Other paths continue analysis.", getPathKey, options.MaximumSlowFingerprintRequestsPerPath);
                    }
                    Console.WriteLine("{0}: {1}, {2} bytes", displayKey, CacheRecommendation.FingerprintTooSlow, bytes.Length);
                    return response;
                }

                // Hashing already started; allow late observations and clean up in finally.
                var observation = history.Observe(requestId, fingerprint, bytes.LongLength, DateTimeOffset.UtcNow, options);
                var analysis = new ResponseAnalysis(key, observation.Recommendation, bytes.LongLength, watch.Elapsed, fingerprint,
                    includeFullRequestKeyInLogs: options.IncludeFullRequestKeyInLogs);
                if (observation.Recommendation != CacheRecommendation.CacheCandidate ||
                    (Volatile.Read(ref analysisStopped) == 0 && Candidates.TryAdd(analysis)))
                    Console.WriteLine("{0}: {1}, {2} bytes", displayKey, observation.Recommendation, bytes.Length);
                if (!StopAtCandidateCapacity()) StopAtCapacity();
                return response;
            }
            finally
            {
                if (Volatile.Read(ref analysisStopped) != 0) ClearAnalysis();
            }
        }

        private bool StopAtCandidateCapacity()
        {
            if (Candidates.Count <= options.MaximumCacheCandidates) return false;
            if (Interlocked.Exchange(ref analysisStopped, 1) == 0)
            {
                ClearAnalysis();
                Console.WriteLine("Cache analysis stopped after exceeding {0} cache candidates. Findings preserved; rerun analysis after relevant data or application changes.", options.MaximumCacheCandidates);
            }
            return true;
        }

        private bool StopAtCapacity()
        {
            if (history.GetCountDistinctRequests() < options.MaximumDistinctRequests) return false;
            if (Interlocked.Exchange(ref analysisStopped, 1) == 0)
            {
                ClearAnalysis();
                Console.WriteLine("Cache analysis stopped at the distinct request key limit of {0}. History cleared; subsequent requests bypass analysis.", options.MaximumDistinctRequests);
            }
            return true;
        }

        private void ClearAnalysis()
        {
            history.Clear();
            slowFingerprints.Clear();
        }
    }
}
