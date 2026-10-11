# AI Context

This file records the working decisions and important question/answer context for CacheAnalyzer. Update it as the design evolves.

## Goal

Build a reusable library that analyzes repeated method or HTTP responses, identifies useful caching opportunities, without altering API requests or responses without coupling the core domain to one web framework.

The initial integration target is inbound ASP.NET Core controllers and minimal APIs.
Outbound `HttpClient` and typed API-client analysis is deferred and is not part of the initial scope.

## Decisions

- Separate framework-neutral analysis from framework adapters.
- `CacheAnalyzer.Core` targets `netstandard2.0` so it can be reused by modern .NET and a future .NET Framework adapter.
- `CacheAnalyzer.AspNetCore` observes HTTP responses and publishes analysis; it does not alter HTTP caching behavior.
- `CacheAnalyzerHelper` currently owns the Metalama `[CacheAnalyzer]` method marker.
- A future `CacheAnalyzer.AspNet` adapter can implement System.Web behavior without changing the core.
- Metalama template source is compile-time code. Runtime template statements are woven into the target method and can be debugged in `obj/LamaDebug/<TFM>/metalama`.
- `LamaDebug` is the Metalama build configuration used to emit readable transformed source and matching debug symbols.

## HTTP Semantics

- Final user decision: analysis only. Preserve request/response headers, payloads, and status. Do not emit ETags, evaluate If-None-Match, or generate 304 responses. Preserve application-owned caching behavior.
- Fingerprints are internal to history and reporting. The endpoint still runs and returns its full original response.
- Current adapters buffer content, which adds memory/latency overhead. Streaming and custom response body features need further compatibility work; do not claim zero behavioral impact for arbitrary APIs.
- The preferred marker is [CacheAnalyze]; [CacheAnalyzer] remains an obsolete alias.

## Analysis And Reporting

- `MinimumObservationsPerKey` defaults to 10 retained observations within the window for each key. `MaximumObservationsPerKey` remains 1,000. The minimum controls recommendation eligibility; the maximum controls retention. Samples explicitly use a minimum of 3 for quick demonstrations.

- Analyze responses independently of whether the client sent a conditional header.
- Recommend caching when the same request key repeatedly produces the same response fingerprint.
- Avoid analysis for payloads over a configurable size limit.
- Measure fingerprint generation and report slow fingerprints without affecting responses. Initially exclude exact slow keys. Once a GET origin/path exceeds MaximumSlowFingerprintRequestsPerPath (default 500) distinct slow query variants, exclude its entire path; other paths continue and history is preserved. Do not collapse path IDs into route templates. QUERY keeps exact body/metadata identity without path escalation. MaximumSlowFingerprintRequests (default 500) bounds groups: each GET origin/path or exact QUERY body/metadata key counts once. Another group beyond capacity stops all analysis, logs once, and clears history and exclusions until pipeline restart. No eviction or retry; excluded paths still count toward capacity. Escalated groups release exact keys. Concurrent limits and accounting are best effort. Duration includes scheduling/setup effects and does not prove caching would be unsuitable.
- Publish observations through framework-neutral sink contracts.
- ASP.NET Core supplies an `ILogger` sink by default. Applications may add sinks for console, files, Splunk, or other systems through dependency injection.
- Analysis can be selected globally for all GET/QUERY requests or restricted to endpoints marked with `[CacheAnalyze]`.
- A reloadable `Enabled` option and a runtime control service can bypass analysis immediately. Disabled requests must avoid response buffering and hashing.
- Capacity policy belongs to the adapter, not `Observe`. History exposes `GetCountDistinctRequests()` and `Clear()`. At the distinct-key limit, stop all analysis for the adapter instance's lifetime, log once at Information, clear history, and bypass subsequent requests before buffering or hashing. Re-enabling runtime control does not reset capacity shutdown. In-flight work may finish; final cleanup releases any late observations. No per-request capacity recommendation is emitted.
- Use `Volatile.Read` for cross-thread visibility of shutdown flags and the history table reference, with `Interlocked.Exchange` for the one-time shutdown transition and history table replacement. These do not serialize requests or make observation accounting exact; in-flight work and approximate history remain acceptable.
- Keep adapter shutdown checks minimal: flag and capacity at entry, flag after the endpoint returns, capacity after observation, and flag in final cleanup. Do not recheck between hashing, observation, and reporting; late work is accepted and cleaned up. Exact/path exclusions are checked at entry, with duplicate accounting handled by the exclusion store.
- `MaximumDistinctRequests` (renamed from `MaximumTrackedRequests`) defaults to 350. `MaximumObservationsPerKey` defaults to 1,000 and must be positive. Use `ConcurrentQueue` history, prune expired queue-head and oldest excess entries, and compare all fingerprints in a capped, time-filtered snapshot with the current response.
- User rule: no application-level locking anywhere; see [AGENTS.md](../AGENTS.md). Do not add locks, semaphores, or equivalent serialization to obtain exact analysis consistency. Approximate snapshots, over-pruning, temporary queue overshoot, and concurrent key-admission overshoot are accepted. Concurrent collections are allowed even though they may synchronize internally. Do not promise strict concurrent bounds or formal lock-freedom.
- A cache recommendation should require both repeated requests within an observation window and a useful payload-to-fingerprint size ratio. The initial proposed ratio is at least 10:1; isolated traffic such as one request per day is not a cache candidate.

## Current Usage

```csharp
builder.Services.AddCacheAnalyzer();

app.UseRouting();
app.UseCacheAnalyzer();
app.MapControllers();
```

```csharp
[HttpGet("{id:int}")]
[CacheAnalyzer]
public Product Get(int id) => repository.Get(id);
```

## Open Work

- Add a .NET Framework/System.Web adapter when its exact target framework is selected.
- Validate behavior for authorization, cookies, `Vary`, compression, streaming responses, QUERY requests, HEAD bypass, errors, cancellation, and concurrent requests.
- Benchmark response buffering, hashing, allocations, and reporting overhead across realistic payload distributions.
- Package and test each independently useful assembly through a local NuGet feed before publishing a prerelease version.

## Product Positioning

Position CacheAnalyzer primarily as a cache-opportunity analyzer that preserves API responses, not as a replacement for ASP.NET Core output caching. Its distinctive value is observing repeated endpoint behavior and publishing structured recommendations through pluggable integrations.

ASP.NET Core already generalizes cache execution through global and named output-cache policies, vary-by rules, storage abstractions, size limits, and endpoint conventions. CacheAnalyzer remains separate: it observes newly generated endpoint responses, and reports caching opportunities without storing backend response bodies.

## Clarifications

### ASP.NET Core endpoints versus a custom HTTP client/service layer

The selected first target is ASP.NET Core controllers and minimal APIs. CacheAnalyzer observes inbound requests handled by the server and analyzes the API responses sent to callers.

A custom HTTP client/service layer would instead mean application-owned code around outbound `HttpClient` calls or internal service methods, such as a typed `ProductsApiClient` that calls another API. That is not the initial target.
