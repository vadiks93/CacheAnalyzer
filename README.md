# CacheAnalyzer

CacheAnalyzer identifies caching opportunities in ASP.NET Core MVC controllers and Minimal APIs by comparing generated response payloads across requests. Findings are advisory: the library does not cache responses, skip endpoint execution, emit ETags, or change HTTP headers, status codes, or bodies.

Licensed under the [MIT License](LICENSE). The ASP.NET Core package targets .NET 10.

## Quick start

Install the package from NuGet:

```shell
dotnet add package CacheAnalyzer.AspNetCore
```

Register the services and place the middleware after routing in `Program.cs`:

```csharp
using CacheAnalyzer.AspNetCore.Extensions;

builder.Services.AddCacheAnalyzer();
builder.Services.AddControllers();

var app = builder.Build();
app.UseRouting();
app.UseCacheAnalyzer();
app.MapControllers();
app.Run();
```

Opt in individual controller actions with `[CacheAnalyze]`:

```csharp
using CacheAnalyzerHelper.Attributes;

[HttpGet("{id:int}")]
[CacheAnalyze]
public Product Get(int id) => repository.Get(id);
```

For Minimal APIs, attach the same metadata:

```csharp
app.MapGet("/products/{id:int}", (int id) => repository.Get(id))
   .WithMetadata(new CacheAnalyzeAttribute());
```

Only GET and QUERY requests with a **200 OK** response are analyzed. All other status codes, including 201, 204, 206, 404, and 500, are ignored even if their payload matches a 200 response. Responses with `Cache-Control: no-store` are also ignored. HEAD and other methods bypass analysis.

Different URLs and query strings have separate histories. QUERY identities also include the request-body hash and content metadata; the body is buffered and rewound before the endpoint runs.

## Configuration

Configure options at registration, or bind them to a reloadable `CacheAnalyzer` configuration section:

```csharp
using CacheAnalyzer.Core.Models;
using CacheAnalyzer.Core.Options;

builder.Services.AddCacheAnalyzer(options =>
{
    options.EndpointSelection = EndpointSelectionMode.AllGetAndQueryRequests;
    options.MaximumPayloadBytes = 1024 * 1024;
    options.MinimumObservationsPerKey = 10;
});

// Alternatively, register defaults and bind configuration:
// builder.Services.AddCacheAnalyzer();
// builder.Services.Configure<CacheAnalysisOptions>(
//     builder.Configuration.GetSection("CacheAnalyzer"));
```

`AttributedEndpoints` is the default selection mode. Choose `AllGetAndQueryRequests` to include unmarked GET and QUERY endpoints.

| Option                                  | Default | Purpose                                                                                              |
| --------------------------------------- | ------- | ---------------------------------------------------------------------------------------------------- |
| `Enabled`                               | `true`  | Enable or temporarily disable analysis.                                                              |
| `MaximumPayloadBytes`                   | 3 MiB   | Maximum response size eligible for fingerprinting.                                                   |
| `MaximumFingerprintDuration`            | 25 ms   | Exclude requests whose completed fingerprint generation exceeds this duration.                       |
| `MinimumObservationsPerKey`             | 10      | Matching observations needed before recommending a candidate.                                        |
| `MaximumObservationsPerKey`             | 1,000   | Retained observations per request identity; keep at least the minimum above.                         |
| `ObservationWindow`                     | 4 hours | Time window used to evaluate retained observations.                                                  |
| `MinimumPayloadToFingerprintRatio`      | 10      | Minimum payload size relative to the fingerprint size for a candidate.                               |
| `MaximumDistinctRequests`               | 350     | Distinct observed request identities before analysis stops.                                          |
| `MaximumCacheCandidates`                | 200     | Candidate capacity; exceeding it stops analysis.                                                     |
| `MaximumSlowFingerprintRequests`        | 500     | Slow request groups before analysis stops: each GET origin/path or exact QUERY identity counts once. |
| `MaximumSlowFingerprintRequestsPerPath` | 500     | Distinct slow GET query variants before excluding the entire origin/path.                            |
| `IncludeFingerprintInLogs`              | `false` | Include response fingerprints in default logs.                                                       |
| `IncludeFullRequestKeyInLogs`           | `false` | Retain and log full request details, including URL/query values.                                     |

Capacity limits and retention are best effort under concurrency; in-flight requests may finish and temporarily exceed limits. Recommendations use point-in-time observations without application-level locking. Concurrent collections may synchronize internally.

Capacity shutdown clears observation history and slow exclusions, but preserves candidate findings. Restart the application with fresh analysis services to begin another run; increasing limits or toggling `Enabled` does not reset a stopped run. A slow fingerprint excludes its request identity from later analysis; the duration limit does not interrupt hashing already in progress.

To temporarily disable or re-enable analysis at runtime:

```csharp
using CacheAnalyzer.AspNetCore.Services.Runtime;

var control = app.Services.GetRequiredService<ICacheAnalyzerRuntimeControl>();
control.Disable();
control.Enable();
```

## Findings and logging

The default sink writes structured events through the application's existing `ILogger` providers:

| Recommendation                             | `Cacheable` | Meaning                                                                         |
| ------------------------------------------ | ----------- | ------------------------------------------------------------------------------- |
| `CacheCandidate`                           | `1`         | Enough retained responses match and the payload-to-fingerprint ratio qualifies. |
| `ResponseChanged`                          | `0`         | Response bytes differ.                                                          |
| `PayloadTooLarge` / `FingerprintTooSlow`   | `0`         | Analysis exceeded the configured size or duration limit.                        |
| `FirstObservation` / `InsufficientTraffic` | `unknown`   | More observations are needed.                                                   |

Candidate, oversized, and slow-fingerprint events use Information; changed and pending findings use Debug. These values describe analysis results, not HTTP caching directives. Ignored responses produce no analysis findings.

Events include `Endpoint` (method and route template), `fingerprintRequest` (hashed request identity), `Cacheable`, `Recommendation`, `PayloadBytes`, `FingerprintMilliseconds`, and `TrackingCapacityReached`. Full URLs and response fingerprints are omitted by default. Either logging option can also be enabled per endpoint with `[CacheAnalyze(IncludeFingerprintInLogs = true, IncludeFullRequestKeyInLogs = true)]`, or equivalent Minimal API metadata. Global and endpoint opt-ins are additive.

The analyzer logs an initial start, capacity shutdown, and request-observed temporary stop/resume transitions. Timestamps and output format depend on your logging provider. For JSON console output with UTC timestamps and all analysis events:

```json
{
  "Logging": {
    "Console": {
      "FormatterName": "json",
      "FormatterOptions": {
        "TimestampFormat": "yyyy-MM-ddTHH:mm:ss.fffZ",
        "UseUtcTimestamp": true
      }
    },
    "LogLevel": {
      "CacheAnalyzer.AspNetCore.Services.Reporting.LoggingResponseAnalysisSink": "Debug"
    }
  }
}
```

Read the retained candidate findings through dependency injection:

```csharp
using CacheAnalyzer.Core.Services.Candidates;

var candidates = app.Services.GetRequiredService<CacheCandidateStore>().GetSnapshot();
```

A candidate is reported once, then its request identity bypasses further response analysis. Findings are historical and are not revalidated when later payloads or status codes change. Restart analysis after relevant application/data changes. Findings are in memory and do not survive restarts.

For custom reporting, implement `IResponseAnalysisSink` from `CacheAnalyzer.Core.Services.Reporting` and register it with `builder.Services.AddSingleton<IResponseAnalysisSink, YourSink>();`. Your sink receives `ResponseAnalysis` from `CacheAnalyzer.Core.Models` and controls its own data disclosure.

## Practical limits and privacy

Prefer ordinary, finite responses with small payloads. Response forwarding preserves content, but capturing copies, buffering QUERY bodies, hashing, and reporting add allocation and timing overhead. `MaximumPayloadBytes` bounds the ASP.NET Core response capture per request; it is not a total memory limit or a QUERY buffering limit. The .NET Framework sample checks response size after buffering.

Avoid indefinite streaming, server-sent events, large downloads, and endpoints requiring prompt flushed delivery. Fingerprinting requires a completed response and can delay request completion. Explicit response start/completion, file sending, custom response-body features, and middleware ordering may require compatibility checks in your pipeline. Synchronous writes remain subject to the server's synchronous-I/O policy.

Comparison is byte-for-byte. Timestamps, random IDs, and randomized payload encryption can change fingerprints even when business data is stable. Randomized QUERY bodies also create separate identities. The analyzer does not normalize fields or decrypt payloads; HTTPS transport encryption does not affect this comparison.

Built-in analysis has no outbound transport. Fingerprints and history stay in the host process unless logging providers or custom services export them. SHA-256 fingerprints are not encryption or guaranteed anonymization. Full request logging can disclose sensitive query values; route templates can also contain sensitive literals. Dumps, paging, and temporary QUERY buffering files may contain analysis/request data; clearing buffers does not securely erase it.

## Samples and publishing

See [samples](samples/README.md) for runnable ASP.NET Core and .NET Framework 4.8 Web API examples. Outbound `HttpClient` and internal service calls are outside the current scope.
