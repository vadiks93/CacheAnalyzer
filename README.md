# CacheAnalyzer

CacheAnalyzer separates response analysis from framework-specific HTTP behavior.

## Scope

The initial version supports inbound ASP.NET Core requests handled by:

- MVC and API controllers
- Minimal API endpoints

It analyzes newly generated responses and reports caching opportunities without adding response headers, changing status codes, or modifying payloads. Fingerprints stay internal: no ETags are emitted and no conditional responses are generated. It does not cache endpoint response bodies or use ASP.NET Core Output Caching.

Outbound `HttpClient`, typed API-client, and internal service-call analysis are not part of the initial scope. These integrations may be considered later without changing the core response-analysis contracts.

## Projects

Runnable ASP.NET Core (.NET 10) and .NET Framework 4.8 Web API samples, request examples, and smoke checks are in [samples](samples/README.md).

- `CacheAnalyzer.Core` targets `netstandard2.0` and owns response fingerprinting.
- `CacheAnalyzerHelper` contains the Metalama `[CacheAnalyzer]` method marker.
- `CacheAnalyzer.AspNetCore` observes eligible HTTP responses and reports analysis through configured sinks.

## ASP.NET Core setup

Register CacheAnalyzer in `Program.cs`:

```csharp
using CacheAnalyzer.AspNetCore;

builder.Services.AddCacheAnalyzer(options =>
{
    options.Enabled = true;
    options.EndpointSelection = EndpointSelectionMode.AttributedEndpoints;
    options.MaximumPayloadBytes = 1024 * 1024;
    options.MaximumFingerprintDuration = TimeSpan.FromMilliseconds(25);
    options.MaximumSlowFingerprintRequests = 500;
    options.SlowFingerprintVariantsPerPath = 50;
    options.MaximumDistinctRequestKeys = 350;
    options.MaximumObservationsPerKey = 1_000;
    options.MinimumObservationsPerKey = 10;
    options.ObservationWindow = TimeSpan.FromHours(1);
    options.MinimumPayloadToFingerprintRatio = 10;
});

var app = builder.Build();

app.UseRouting();
app.UseCacheAnalyzer();

app.MapControllers();
```

Place the middleware after routing so endpoint metadata is available. Mark GET endpoints that should be analyzed:

```csharp
[HttpGet("{id:int}")]
[CacheAnalyze]
public Product Get(int id) => repository.Get(id);
```

Use `EndpointSelectionMode.AttributedEndpoints` for one-by-one opt-in through `[CacheAnalyze]`, or `EndpointSelectionMode.AllGetAndQueryRequests` to analyze every GET and QUERY endpoint.

HEAD and other methods bypass analysis. QUERY analysis keys include a SHA-256 hash of the request body plus its content type, encoding, and language so different queries at the same URL have separate histories. The request body is buffered and rewound before the endpoint runs.

`Enabled` can be supplied through reloadable configuration using the standard options system. The behavior can also be stopped immediately at runtime; disabled requests bypass response buffering and hashing:

```json
{
  "CacheAnalyzer": {
    "Enabled": true,
    "EndpointSelection": "AttributedEndpoints"
  }
}
```

```csharp
builder.Services.AddCacheAnalyzer();
builder.Services.Configure<CacheAnalysisOptions>(
    builder.Configuration.GetSection("CacheAnalyzer"));
```

With the default ASP.NET Core configuration providers, changes to a reloadable configuration source are observed through `IOptionsMonitor` without restarting the application.

```csharp
var control = app.Services.GetRequiredService<ICacheAnalyzerRuntimeControl>();
control.Disable();

// Re-enable later if required.
control.Enable();
```

Analysis runs independently of client conditional headers. The API retains ownership of its status, headers, and complete body, including any existing ETag or 304 behavior. A response becomes a cache candidate when enough retained observations match and the payload is sufficiently larger than its internal fingerprint. Oversized payloads and slow fingerprint generation are reported without affecting the response.

The current adapter buffers responses before forwarding them and buffers QUERY input to calculate a key. This adds memory and latency overhead; MaximumPayloadBytes limits hashing, not response buffering. Streaming, explicit flush/start operations, and custom body features need further compatibility work before enabling analysis broadly. Prefer endpoint opt-in for ordinary buffered APIs.

When fingerprint generation exceeds `MaximumFingerprintDuration`, the adapter reports `FingerprintTooSlow` and excludes that exact request key from later response analysis. GET exclusions include the full query string and are checked before response buffering or hashing. After `SlowFingerprintVariantsPerPath` (default 50) distinct slow GET URLs share the same origin and path, all GET query variants for that path are excluded, including variants that could be cheaper. Only query strings are ignored for this escalation: different path IDs and hosts remain separate. Repeats of already excluded requests do not increment the count. QUERY exclusions retain body and content-metadata identity; forming that key still requires request-body buffering and hashing, and QUERY requests do not escalate to path exclusions.

The slow-request store retains up to `MaximumSlowFingerprintRequests` (default 500) exact keys, independently of normal observation history. Another distinct slow key triggers one Information log, clears history and exclusions, and permanently bypasses the analyzer. Path counters are also bounded by this store. Excluded keys remain retained after path escalation so repeated escalation across different paths cannot evade the overall limit. Concurrent admission may temporarily overshoot and in-flight analysis may finish. Exclusions have no automatic retry or expiry; restart the pipeline to begin fresh.

The duration check measures elapsed time after fingerprint generation finishes; it cannot interrupt hashing already underway. CPU contention, thread scheduling, GC pauses, first-call initialization/JIT, and custom provider work can contribute to the measured duration even below the payload-size limit. The built-in provider measures hashing plus its setup and fingerprint-string conversion, not endpoint execution or response buffering. Slow fingerprint analysis does not establish that the API would be a poor caching candidate.

The middleware defaults to a limit of 350 distinct request keys (`MaximumDistinctRequestKeys`, formerly `MaximumTrackedRequests`), with history retaining up to 1,000 observations per key (`MaximumObservationsPerKey`). `MinimumObservationsPerKey` defaults to 10 and controls the evidence needed for a recommendation; keep it at or below the observation maximum. The middleware checks `GetCountDistinctRequests()` before analysis and observation, and after recording an observation. Once the limit is detected, it logs one Information message, clears history, and permanently bypasses analysis for that middleware instance. Later requests go directly to the next handler without analyzer buffering, hashing, observations, or reports. Runtime enablement or increasing the limit does not reset this stop; recreate the pipeline (normally by restarting the application) to analyze again. The middleware remains registered in the built pipeline.

Capacity checks are best effort: concurrent requests can temporarily exceed the limit, and work or reports already in flight may finish. Retained history is cleared again as those requests finish, allowing its memory to be reclaimed by GC; this does not force immediate process-memory reduction or clear data retained by custom sinks. `IResponseFingerprintHistory` exposes `GetCountDistinctRequests()` and `Clear()`; custom implementations must implement these methods, and direct callers must enforce their own distinct-key capacity policy. The .NET Framework sample handler applies the same stop-and-bypass policy.

Each observation prunes expired queue-head entries and oldest excess entries. Analysis uses a capped snapshot filtered to the observation window, including when timestamps arrive out of order. A cache candidate requires enough snapshot entries, all matching the current fingerprint, and a useful payload-to-fingerprint ratio. Set `MaximumObservationsPerKey` to a positive value at least as large as `MinimumObservationsPerKey`. Response bodies are never stored in history.

No application-level locks are used: concurrent queues and snapshots deliberately provide approximate analysis. Concurrent admission can exceed the distinct-key limit; concurrent queue updates can temporarily exceed the observation limit or discard extra entries. The default retention target is 200,000 observations, not a strict concurrent ceiling. Expired entries behind a newer queue head are excluded from analysis and reclaimed as the queue advances. Lowering the key limit does not evict existing keys; a changed observation limit applies when a key is next observed.

The repository's [no-locking rule](AGENTS.md) prohibits adding locks or equivalent serialization for exact analysis consistency. Built-in concurrent collections are allowed, but may synchronize internally; this is not a claim that every operation is formally lock-free.

Add any number of custom reporting integrations through dependency injection:

```csharp
builder.Services.AddSingleton<IResponseAnalysisSink, SplunkResponseAnalysisSink>();
```

An integration implements the framework-neutral contract:

```csharp
public sealed class SplunkResponseAnalysisSink : IResponseAnalysisSink
{
    public void Report(ResponseAnalysis analysis)
    {
        // Send the structured observation to the configured Splunk client.
    }
}
```

This utility identifies opportunities to add caching elsewhere. It does not itself save response bandwidth, cache responses, or avoid endpoint execution.
