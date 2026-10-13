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

### Folder and namespace organization

Namespaces follow each project's folders. Core result types and enums live in `CacheAnalyzer.Core.Models`, configuration in `CacheAnalyzer.Core.Options`, and services in `CacheAnalyzer.Core.Services` subfolders: `Fingerprinting`, `History`, `Candidates`, `Exclusions`, and `Reporting`. Service interfaces share their implementation's folder; the reporting contract is in Core and its logging implementation is in the ASP.NET Core adapter.

The adapter groups types under `Middleware`, `IO`, `Extensions`, `Services.Runtime`, and `Services.Reporting`. Method attributes live in `CacheAnalyzerHelper.Attributes`. Samples separate `Controllers`, `Models`, and framework `Handlers`; shared payload generation uses `CacheAnalyzer.Samples.Shared.Services`. Tests mirror the components they cover.

This namespace reorganization is a breaking change for consumers: update imports and fully qualified type names to the new namespaces. The logging category is now `CacheAnalyzer.AspNetCore.Services.Reporting.LoggingResponseAnalysisSink`. Analysis behavior is unchanged.

## ASP.NET Core setup

Register CacheAnalyzer in `Program.cs`:

```csharp
using CacheAnalyzer.AspNetCore.Extensions;
using CacheAnalyzer.AspNetCore.Services.Runtime;
using CacheAnalyzer.Core.Models;
using CacheAnalyzer.Core.Options;
using CacheAnalyzerHelper.Attributes;

builder.Services.AddCacheAnalyzer(options =>
{
    options.Enabled = true;
    options.EndpointSelection = EndpointSelectionMode.AttributedEndpoints;
    options.MaximumPayloadBytes = 1024 * 1024;
    options.MaximumFingerprintDuration = TimeSpan.FromMilliseconds(25);
    options.MaximumSlowFingerprintRequestsPerPath = 500;
    options.MaximumSlowFingerprintRequests = 500;
    options.MaximumDistinctRequests = 350;
    options.MaximumCacheCandidates = 200;
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
    "MaximumCacheCandidates": 200,
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

Once a request key qualifies as `CacheCandidate`, its first finding is retained separately and reported once. Later matching requests bypass response capture, fingerprinting, and history evaluation. GET query variants and QUERY body/content-metadata variants remain separate keys; identifying a QUERY key still requires request-body buffering and hashing. Candidate findings contain analysis metadata and a fingerprint, not response bodies. Inspect a point-in-time snapshot through the registered singleton:

```csharp
var candidates = app.Services.GetRequiredService<CacheCandidateStore>().GetSnapshot();
```

`MaximumCacheCandidates` defaults to 200 and can be set in app configuration as above. Analysis stops permanently for that middleware instance after the limit is exceeded: sequentially, the 201st distinct candidate is retained and reported, then observation history and slow exclusions are cleared. Candidate findings remain available even when another analysis capacity limit stops the run. Later requests bypass analysis; increasing the limit or toggling runtime enablement does not restart a stopped run. Admission and shutdown are best effort under concurrency: requests already in progress may finish analysis, and simultaneous candidate admissions can temporarily exceed the configured limit. No application-level locks serialize requests.

Candidates are historical findings from this run, not continuously revalidated classifications. Later response changes for excluded keys will not be detected. Rerun analysis after relevant data or application changes, or after changing evaluation settings, by recreating the pipeline with fresh analysis services (normally restarting the application). The candidate store is in-memory and is not preserved across restarts.

The default logging sink includes a structured string property `Cacheable`, rendered as `cacheable=1` for `CacheCandidate`, `cacheable=0` for `ResponseChanged`, `PayloadTooLarge`, or `FingerprintTooSlow`, and `cacheable=unknown` for `FirstObservation` or `InsufficientTraffic`. This is an advisory analysis result under the configured limits, not an HTTP caching directive or proof that caching is impossible. Candidate, oversized, and slow-fingerprint events use Information; changed-response and pending-evidence events use Debug, so enable Debug for this sink to see those events. Excluded or bypassed requests do not produce new analysis events.

Every analysis event exposes separate structured fields: `Endpoint`, `fingerprintRequest`, `Cacheable`, `Recommendation`, `PayloadBytes`, `FingerprintMilliseconds`, and `TrackingCapacityReached`. `Endpoint` is the method plus declared route template (for example `GET /api/products/{id}`), without actual route-parameter or query values. If route metadata is unavailable, it uses the method plus `(unmapped endpoint)` rather than exposing the actual path. `fingerprintRequest` is a SHA-256/Base64URL hash of the complete request key, preserving separate identities for different origins, literal paths, query strings, and QUERY bodies/content metadata. Recommendations are strings; byte counts and duration are numeric; the capacity flag is boolean. `fingerprintResponse` and `FullRequestKey` are omitted from both the message and structured state by default. The ASP.NET Core sample uses JSON console output, with these fields under `State` alongside the logger's timestamp, category, level, and message. To enable the same format in a consuming ASP.NET Core application's default console provider, add this configuration:

```json
{
  "Logging": {
    "Console": {
      "FormatterName": "json",
      "FormatterOptions": {
        "TimestampFormat": "yyyy-MM-ddTHH:mm:ss.fffZ",
        "UseUtcTimestamp": true,
        "IncludeScopes": true
      }
    },
    "LogLevel": {
      "CacheAnalyzer.AspNetCore.Services.Reporting.LoggingResponseAnalysisSink": "Debug"
    }
  }
}
```

Console events remain one JSON object per line for ingestion by systems such as Splunk. The library supplies structured properties through `ILogger`; the application controls provider/output formatting, so existing logging providers are not replaced. JSON is not serialized into an embedded message string.

To explicitly include the internal response fingerprint in logs for all analyzed endpoints, set `options.IncludeFingerprintInLogs = true` in `AddCacheAnalyzer`, or set `"IncludeFingerprintInLogs": true` under the bound `CacheAnalyzer` configuration section. Alternatively, opt in for a single controller action:

```csharp
[HttpGet("{id:int}")]
[CacheAnalyze(IncludeFingerprintInLogs = true)]
public Product Get(int id) => repository.Get(id);
```

Minimal API endpoints can opt in through `.WithMetadata(new CacheAnalyzeAttribute { IncludeFingerprintInLogs = true })`. Either the global option or endpoint opt-in enables the field; an attribute's default `false` does not override a global opt-in. This affects the default logging sink only. Fingerprints remain available internally in analysis results, candidate snapshots, and custom sinks; custom sinks control their own disclosure. When enabled, `fingerprintResponse` is a separate nullable string field (null when no fingerprint was generated). Existing candidate exclusions still apply; enabling logging later does not reanalyze excluded keys.

Full request details have an independent opt-in: `IncludeFullRequestKeyInLogs` defaults to `false`. Enable it through `AddCacheAnalyzer` options, the bound configuration section, or `[CacheAnalyze(IncludeFullRequestKeyInLogs = true)]`. Minimal API metadata uses the same named property. Either the global or endpoint opt-in retains the full key in `ResponseAnalysis.FullRequestKey` and emits a separate `FullRequestKey` log field. The full key contains method, URL, query values, and for QUERY requests the body hash and content metadata; it never contains the raw QUERY body. Fingerprint logging and full-request logging are independent:

```json
{
  "CacheAnalyzer": {
    "IncludeFullRequestKeyInLogs": false,
    "IncludeFingerprintInLogs": false
  }
}
```

The built-in adapters use hashed identities for observation history, candidate lookup, and slow exclusions (including GET path groups). `ResponseAnalysis.FingerprintRequest` exposes that identity; `RequestKey` is now an alias for `FingerprintRequest`, not the previous full request string. `ResponseAnalysis.FingerprintResponse` exposes the response-body hash. The JSON fields are named `fingerprintRequest` and `fingerprintResponse`, replacing the earlier `RequestId` and `Fingerprint` fields. Candidate snapshots retain the safe endpoint label and omit `FullRequestKey` unless opted in. This is a behavior/schema change for consumers of the old properties and log fields. Direct callers of history or slow-exclusion services choose their own keys; those services do not automatically hash arbitrary caller-supplied keys. Matching uses the full request identity, not the route template, so grouping the readable labels does not merge different query variants. Opting in after a key was excluded does not recreate its earlier finding; start a fresh analysis run to reassess it.

## Privacy and data handling

Analysis runs locally within the host application, with no built-in outbound transport. The built-in runtime adapters do not initiate outbound HTTP calls to send analysis data. The default history and candidate store retain fingerprints in managed process memory on the host's machine; fingerprints are excluded from default logs unless explicitly enabled. Configured logging providers, custom sinks, and replacement analysis services can export or persist data, so the host application controls those destinations. This describes the built-in analysis behavior, not a guarantee about every dependency or the host application's other activity.

In-process storage is not a guarantee that bytes remain exclusively in RAM or are inaccessible. Process/crash dumps and operating-system paging can contain fingerprints, request keys, and temporarily captured response bytes. QUERY request-body buffering may also use temporary files through ASP.NET Core's buffering facilities. Clearing collections or disposing buffers does not securely erase their contents or force immediate reclamation. Access to dumps, paging storage, logs, and temporary files is governed by the client's hosting environment.

The built-in fingerprint provider uses SHA-256, a one-way hash of response bytes, not encryption or guaranteed anonymization. Predictable payloads or request keys can be guessed and hashed for comparison. Full request keys are constructed transiently to compute identities; raw request data also remains part of the host's normal request processing and can appear in dumps. By default, built-in analysis stores retain hashed request/path identities and safe route labels, and the logging sink emits those rather than full request details. Explicitly enabling full-request logging also retains those details in candidate findings. Declared route templates can themselves contain sensitive literals; choose route definitions accordingly. Other application/server logs, configured scopes, custom sinks, and exceptions may disclose request details independently of these options.

The ASP.NET Core adapter forwards every response write to the original stream and retains a separate copy up to `MaximumPayloadBytes` for analysis. It checks the declared Content-Length (when available) and accumulated size when capturing each write. If either exceeds the limit, it discards the analysis copy and skips fingerprinting; response forwarding continues without replaying bytes. It does not change headers, status, or payload. Responses exactly at the limit remain eligible. Capture capacity is capped at the configured limit (and the runtime array limit); eligible responses still allocate a second byte-array copy for fingerprinting. This is a per-response bound, not a total memory or concurrent-request limit, and does not limit QUERY request buffering. Copying, allocations, hashing, and stream adaptation add overhead and can affect timing. Hashing and reporting run after the downstream pipeline returns and can delay request completion. The .NET Framework sample adapter still checks payload size after buffering.

### Supported endpoint scope

Prefer endpoint opt-in for ordinary, finite responses with small payloads. Writes and `Flush()` / `FlushAsync()` are forwarded to the original response stream while the analysis copy remains available for fingerprinting. Calling flush alone does not disqualify a response. Do not enable analysis broadly for indefinite streaming APIs or large-content endpoints: analysis needs a completed response, and oversized payloads are not fingerprinted.

Do not opt in endpoints whose response payload or individual fields use randomized application-level encryption when your goal is to assess stability of the underlying plaintext. Fresh nonces/IVs can produce different ciphertext for identical plaintext on each call. CacheAnalyzer compares the response bytes it observes, so these responses can produce `ResponseChanged` even when the underlying values have not changed. Randomized encryption in QUERY request bodies likewise produces different request identities, splitting observations across keys instead of accumulating evidence for one logical query. CacheAnalyzer does not decrypt, normalize, or rewrite encrypted data. This limitation concerns payload/field encryption, not HTTPS/TLS transport encryption, which is outside the application response bytes being analyzed. Stable ciphertext can still be compared byte-for-byte, but matching ciphertext does not establish whether replaying an encrypted response is appropriate for the application's security semantics.

Volatile response fields such as `readAt`, a current timestamp, request IDs, or random values also change the fingerprint even when the business data is unchanged. CacheAnalyzer reports byte-level changes, not semantic differences: it does not parse payload fields or exclude selected properties before hashing. Ignoring a timestamp would require payload-aware normalization for analysis and application-specific decisions about whether that field can safely be replayed. The built-in analyzer deliberately does not do this. Keep these endpoints outside analysis when you only want to evaluate the stable business data. A stored date such as `lastModified` is not inherently a problem if it remains unchanged between matching responses.

**Avoid enabling CacheAnalyzer on endpoints where low time to first byte or prompt delivery of flushed chunks is critical**, such as server-sent events or live incremental responses. Analyzer setup can delay the first response bytes, and copying after a write can delay the next flush or write. Forwarding to the original response stream does not guarantee delivery to the client at that point; other middleware and the server may also buffer data. CacheAnalyzer preserves response content, but does not guarantee unchanged delivery timing. Keep these endpoints outside analysis, or validate the added latency in your actual pipeline before opting them in.

`Content-Length` is optional: responses can be collected across multiple synchronous or asynchronous writes without it. The analyzer checks the declared length when available and always checks the accumulated bytes against `MaximumPayloadBytes`. It never adds or changes `Content-Length`. Multiple writes alone do not identify a streaming API.

The analysis copy is never sent to the client: each chunk is forwarded exactly once during its original write, before being copied for analysis. An endpoint can write and flush its first bytes before producing the rest of the response; forwarding does not wait for the complete response or its fingerprint. Flushing the destination does not clear the analysis copy. However, the analyzer can increase time to first byte: setup runs before the endpoint, and QUERY request-body buffering and hashing also happen before it. Copying after each response write adds overhead and can delay a subsequent flush or write. Forwarding and flushing do not guarantee immediate network delivery. Response fingerprinting runs after the downstream pipeline returns and can delay request completion; indefinite responses cannot be fingerprinted until they finish. Explicit response start/completion (`StartAsync`/`CompleteAsync`), file sending (`SendFileAsync`), custom `IHttpResponseBodyFeature` implementations, and middleware ordering have not been validated for all combinations. Synchronous forwarding also remains subject to the server's synchronous-I/O policy.

When fingerprint generation exceeds `MaximumFingerprintDuration`, the adapter reports `FingerprintTooSlow` and excludes that exact request key from later response analysis. GET exclusions initially include the full query string. Once a GET origin/path exceeds `MaximumSlowFingerprintRequestsPerPath` (default 500) distinct slow query variants, all query variants for that path are excluded. With a limit of 2, the third distinct slow variant excludes the path. Repeated excluded keys do not count again. Different hosts and literal paths remain separate; path IDs are not collapsed into route templates. This per-path exclusion preserves observation history and allows other paths to continue analysis, subject to the overall capacity limits. QUERY exclusions retain exact body and content-metadata identity without path escalation; forming that key still requires request-body buffering and hashing.

`MaximumSlowFingerprintRequests` (default 500) bounds retained slow request groups: each GET origin/path counts once, regardless of its query variants, and each exact QUERY body/metadata key counts once. Once full, another distinct group stops all analysis, logs once, and clears history and exclusions. Groups are never evicted to retry expensive paths. Subsequent requests bypass analysis until the pipeline restarts; increasing the limit or toggling runtime enablement does not reset the stop. An excluded GET path still occupies one group, so escalation cannot evade the overall limit.

Within each GET group, `MaximumSlowFingerprintRequestsPerPath` limits retained distinct slow query variants. Exceeding it releases the exact keys and retains a path exclusion flag. Concurrent admission may temporarily overshoot, counting is best effort, and in-flight analysis may finish; final cleanup clears late retained state. There is no timed retry or expiry. The former `SlowFingerprintVariantsPerPath` setting is replaced by `MaximumSlowFingerprintRequestsPerPath`.

The duration check measures elapsed time after fingerprint generation finishes; it cannot interrupt hashing already underway. CPU contention, thread scheduling, GC pauses, first-call initialization/JIT, and custom provider work can contribute to the measured duration even below the payload-size limit. The built-in provider measures hashing plus its setup and fingerprint-string conversion, not endpoint execution or response buffering. Slow fingerprint analysis does not establish that the API would be a poor caching candidate.

The middleware defaults to a limit of 350 distinct request keys (`MaximumDistinctRequests`, formerly `MaximumTrackedRequests`), with history retaining up to 1,000 observations per key (`MaximumObservationsPerKey`). `MinimumObservationsPerKey` defaults to 10 and controls the evidence needed for a recommendation; keep it at or below the observation maximum. The middleware checks `GetCountDistinctRequests()` at request entry and after recording an observation. After the endpoint returns, it checks only the shutdown flag. Requests already hashing may finish recording and reporting during shutdown; final cleanup releases late observations. Once the limit is detected, it logs one Information message, clears history, and permanently bypasses analysis for that middleware instance. Later requests go directly to the next handler without analyzer buffering, hashing, observations, or reports. Runtime enablement or increasing the limit does not reset this stop; recreate the pipeline (normally by restarting the application) to analyze again. The middleware remains registered in the built pipeline.

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

Deferred ideas, including optional sub-payload mappings for analysis, are tracked in [TODO.md](TODO.md).
