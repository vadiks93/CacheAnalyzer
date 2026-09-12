using System;
using System.Diagnostics;


using System.Net.Http;

using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CacheAnalyzer.Core;
using Newtonsoft.Json;

namespace CacheAnalyzer.Sample.NetFramework
{
    // Sample adapter for these small demo responses; not a general Web API integration.
    public sealed class SampleCacheAnalysisHandler : DelegatingHandler
    {
        private readonly IResponseFingerprintProvider fingerprints = new Sha256ResponseFingerprintProvider();
        private readonly IResponseFingerprintHistory history = new InMemoryResponseFingerprintHistory();
        // Volatile.Read is intentionally omitted: delayed visibility and a few extra or
        // missed observations are acceptable. Only the one-time log needs an atomic transition.
        private int capacityReached;
        private readonly CacheAnalysisOptions options = new CacheAnalysisOptions
        {
            MinimumObservationsPerKey = 3,
            MinimumPayloadToFingerprintRatio = 0,
            MaximumDistinctRequestKeys = 200,
            MaximumObservationsPerKey = 1_000
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (capacityReached != 0)
                return await base.SendAsync(request, cancellationToken);

            var isQuery = request.Method.Method == "QUERY";
            if ((request.Method != HttpMethod.Get && !isQuery) ||
                !request.RequestUri.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal))
                return await base.SendAsync(request, cancellationToken);

            if (StopAtCapacity()) return await base.SendAsync(request, cancellationToken);

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

                var response = await base.SendAsync(request, cancellationToken);
                if (StopAtCapacity() || !response.IsSuccessStatusCode || response.Content == null || response.Headers.CacheControl?.NoStore == true)
                    return response;

                var bytes = await response.Content.ReadAsByteArrayAsync();
                if (bytes.LongLength > options.MaximumPayloadBytes)
                {
                    Console.WriteLine("{0}: {1}, {2} bytes", key, CacheRecommendation.PayloadTooLarge, bytes.Length);
                    return response;
                }
                var watch = Stopwatch.StartNew();
                var fingerprint = fingerprints.CreateFingerprint(bytes);
                watch.Stop();
                if (watch.Elapsed > options.MaximumFingerprintDuration)
                {
                    Console.WriteLine("{0}: {1}, {2} bytes", key, CacheRecommendation.FingerprintTooSlow, bytes.Length);
                    return response;
                }

                if (StopAtCapacity()) return response;
                var observation = history.Observe(key, fingerprint, bytes.LongLength, DateTimeOffset.UtcNow, options);
                if (capacityReached == 0)
                    Console.WriteLine("{0}: {1}, {2} bytes", key, observation.Recommendation, bytes.Length);
                StopAtCapacity();
                return response;
            }
            finally
            {
                if (capacityReached != 0) history.Clear();
            }
        }

        private bool StopAtCapacity()
        {
            if (capacityReached != 0) return true;
            if (history.GetCountDistinctRequests() < options.MaximumDistinctRequestKeys) return false;
            if (Interlocked.Exchange(ref capacityReached, 1) == 0)
            {
                history.Clear();
                Console.WriteLine("Cache analysis stopped at the distinct request key limit of {0}. History cleared; subsequent requests bypass analysis.", options.MaximumDistinctRequestKeys);
            }
            return true;
        }
    }
}
