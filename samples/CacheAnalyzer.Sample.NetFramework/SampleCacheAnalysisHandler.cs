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
        private readonly CacheAnalysisOptions options = new CacheAnalysisOptions
        {
            MinimumObservations = 3,
            MinimumPayloadToFingerprintRatio = 0,
            MaximumDistinctRequestKeys = 200,
            MaximumObservationsPerKey = 1_000
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var isQuery = request.Method.Method == "QUERY";
            if ((request.Method != HttpMethod.Get && !isQuery) ||
                !request.RequestUri.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal))
                return await base.SendAsync(request, cancellationToken);

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
            if (!response.IsSuccessStatusCode || response.Content == null || response.Headers.CacheControl?.NoStore == true)
                return response;

            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.LongLength > options.MaximumPayloadBytes) return response;
            var watch = Stopwatch.StartNew();
            var fingerprint = fingerprints.CreateFingerprint(bytes);
            watch.Stop();
            if (watch.Elapsed > options.MaximumFingerprintDuration) return response;

            var observation = history.Observe(key, fingerprint, bytes.LongLength, DateTimeOffset.UtcNow, options);
            Console.WriteLine("{0}: {1}, {2} bytes", key, observation.Recommendation, bytes.Length);
            return response;
        }
    }
}
