# AI Context

This file records the working decisions and important question/answer context for CacheAnalyzer. Update it as the design evolves.

## Goal

Build a reusable library that analyzes repeated method or HTTP responses, identifies useful caching opportunities, and supports HTTP cache validation without coupling the core domain to one web framework.

The initial integration target is inbound ASP.NET Core controllers and minimal APIs.
Outbound `HttpClient` and typed API-client analysis is deferred and is not part of the initial scope.

## Decisions

- Separate framework-neutral analysis from framework adapters.
- `CacheAnalyzer.Core` targets `netstandard2.0` so it can be reused by modern .NET and a future .NET Framework adapter.
- `CacheAnalyzer.AspNetCore` owns `HttpContext`, middleware, HTTP headers, and `304 Not Modified` behavior.
- `CacheAnalyzerHelper` currently owns the Metalama `[CacheAnalyzer]` method marker.
- A future `CacheAnalyzer.AspNet` adapter can implement System.Web behavior without changing the core.
- Metalama template source is compile-time code. Runtime template statements are woven into the target method and can be debugged in `obj/LamaDebug/<TFM>/metalama`.
- `LamaDebug` is the Metalama build configuration used to emit readable transformed source and matching debug symbols.

## HTTP Semantics

- A server may only return `304 Not Modified` for a conditional HTTP request, normally when the client sends `If-None-Match`.
- Duplicate-response analysis does not require `If-None-Match` and runs for every tagged eligible response.
- The current implementation executes the endpoint before comparing the response. It detects caching potential and saves bandwidth for conditional requests, but does not yet skip endpoint or database execution.
- CacheAnalyzer will not store backend endpoint response bodies or integrate with ASP.NET Core Output Caching.
- For a request without `If-None-Match`, return `200 OK` with both the response payload and an `ETag` response header when analysis is suitable.
- For a request with `If-None-Match`, execute the endpoint, generate the current ETag, and return `304 Not Modified` without a body only when the values match. If they differ, return `200 OK` with the new payload and ETag.
- There is no standard initial request header meaning "generate an ETag for me." `If-None-Match` is usable only after the client has received an ETag.
- Final decision: `[CacheAnalyzer]` always emits an ETag for eligible responses. No custom client opt-in header is needed. Clients may ignore the ETag or return it later through `If-None-Match`.
- The preferred marker name is `[CacheAnalyze]`; `[CacheAnalyzer]` remains as an obsolete compatibility alias.
- The execute-hash-compare ETag flow is stateless across backend instances: every instance can deterministically hash the newly generated response and compare it directly with the browser-provided ETag. Only cross-instance recommendation statistics need optional shared storage.
- Caching responses produced by outbound upstream HTTP services is a possible future, separate feature.

## Analysis And Reporting

- Analyze responses independently of whether the client sent a conditional header.
- Recommend caching when the same request key repeatedly produces the same response fingerprint.
- Avoid analysis for payloads over a configurable size limit.
- Measure fingerprint generation and avoid emitting an ETag when it exceeds a configurable duration.
- Publish observations through framework-neutral sink contracts.
- ASP.NET Core supplies an `ILogger` sink by default. Applications may add sinks for console, files, Splunk, or other systems through dependency injection.
- Analysis can be selected globally for all GET/HEAD requests or restricted to endpoints marked with `[CacheAnalyze]`.
- A reloadable `Enabled` option and a runtime control service can bypass analysis immediately. Disabled requests must avoid response buffering and hashing.
- Keep analysis deliberately small: use a fixed-capacity in-memory table and stop admitting new request keys when it is full instead of building a complex eviction system initially.
- A cache recommendation should require both repeated requests within an observation window and a useful payload-to-ETag size ratio. The initial proposed ratio is at least 10:1; isolated traffic such as one request per day is not a cache candidate.
- ASP.NET Core Output Caching can automatically revalidate a fresh cached response, but an ETag must first be supplied by the application or another component. A named output-cache policy alone does not generate an ETag.
- The browser's `If-None-Match` value and server cache freshness are separate. The browser sends the validator from its prior response; a fresh server Output Cache entry means the server still has an unexpired cached representation available for comparison. An ETag identifies a representation but does not define its expiration.

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
- Validate behavior for authorization, cookies, `Vary`, compression, streaming responses, HEAD requests, errors, cancellation, and concurrent requests.
- Benchmark response buffering, hashing, allocations, and reporting overhead across realistic payload distributions.
- Package and test each independently useful assembly through a local NuGet feed before publishing a prerelease version.

## Product Positioning

Position CacheAnalyzer primarily as a cache-opportunity analyzer with optional standards-based HTTP validation, not as a replacement for ASP.NET Core output caching. Its distinctive value is observing repeated endpoint behavior and publishing structured recommendations through pluggable integrations.

ASP.NET Core already generalizes cache execution through global and named output-cache policies, vary-by rules, storage abstractions, size limits, and endpoint conventions. CacheAnalyzer remains separate: it observes newly generated endpoint responses, performs browser ETag validation, and reports whether client-side validation appears worthwhile without storing backend response bodies.

## Clarifications

### ASP.NET Core endpoints versus a custom HTTP client/service layer

The selected first target is ASP.NET Core controllers and minimal APIs. CacheAnalyzer observes inbound requests handled by the server and analyzes the API responses sent to callers.

A custom HTTP client/service layer would instead mean application-owned code around outbound `HttpClient` calls or internal service methods, such as a typed `ProductsApiClient` that calls another API. That is not the initial target.
