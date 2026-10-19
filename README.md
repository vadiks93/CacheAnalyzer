# CacheAnalyzer

Find caching opportunities in ASP.NET Core MVC and Minimal APIs by comparing response payloads. Analysis is advisory: CacheAnalyzer does not cache responses, emit ETags, or change HTTP headers, status codes, or bodies.

Targets .NET 10. Licensed under [MIT](LICENSE).

## Quick start

```shell
dotnet add package CacheAnalyzer.AspNetCore
```

Register services and place the middleware after routing:

```csharp
using CacheAnalyzer.AspNetCore.Extensions;
using CacheAnalyzerHelper.Attributes;

builder.Services.AddCacheAnalyzer();
builder.Services.AddControllers();

var app = builder.Build();
app.UseRouting();
app.UseCacheAnalyzer();
app.MapControllers();
app.MapGet("/products/{id:int}", (int id) => repository.Get(id))
   .WithMetadata(new CacheAnalyzeAttribute());
app.Run();
```

For controllers, add `[CacheAnalyze]` to individual actions. By default, only marked GET and QUERY endpoints returning **200 OK** are analyzed; `Cache-Control: no-store` responses are ignored. Different URLs/query strings have separate histories. QUERY identities also include body and content metadata; buffered bodies are rewound before endpoint execution.

## Partial data stability

Exclude volatile fields from internal JSON comparison with explicit JSON Pointer paths:

```csharp
app.MapGet("/news", [CacheAnalyze(IgnoreJsonPaths = new[] { "/request_id" })]
    () => GetNews());
```

This leaves the complete response untouched. `/request_id` selects a root property; `/metadata/request_id` selects a nested property. Paths are case-sensitive and have no wildcards. No fields are excluded automatically.

Eligible JSON is normalized by removing selected fields, sorting object properties, and removing insignificant whitespace. Array order and value types remain significant. Unsupported or invalid input falls back to exact-byte comparison; logs identify the comparison mode.

When the included data matches enough retained observations, the result is **`StableDataCandidate`, logged as `cacheable=partial`**. This suggests caching stable data while regenerating request-specific fields; it does not establish that the full response can be replayed. Without exclusions, comparison uses complete response bytes.

## Configuration

Defaults require 10 matching observations within four hours and a payload-to-fingerprint ratio of at least 10. Response capture is limited to 3 MiB; fingerprints taking more than 80 ms are excluded.

```csharp
builder.Services.AddCacheAnalyzer(options =>
{
    options.MinimumObservationsPerKey = 10;
    options.MaximumPayloadBytes = 1024 * 1024;
});
```

Use `EndpointSelectionMode.AllGetAndQueryRequests` to include unmarked endpoints, or bind `CacheAnalysisOptions` to configuration for runtime changes. See the [configuration reference](docs/analysis-reference.md#configuration) for all options, capacity limits, and runtime controls.

## Findings

The default sink uses the application's `ILogger` providers:

| Recommendation | `cacheable` | Meaning |
| --- | --- | --- |
| `CacheCandidate` | `1` | Complete response bytes match and thresholds qualify. |
| `StableDataCandidate` | `partial` | Included JSON data matches after explicit exclusions. |
| `ResponseChanged` | `0` | Compared response data differs. |
| `PayloadTooLarge` / `FingerprintTooSlow` | `0` | Analysis exceeded a configured limit. |
| `FirstObservation` / `InsufficientTraffic` | `unknown` | More observations are needed. |

Candidates and size/duration findings log at Information; changed and pending findings log at Debug. Events include comparison mode, payload size, fingerprint duration, and payload-to-fingerprint ratio. Full URLs and fingerprint values are omitted by default.

Once recorded, a candidate bypasses further analysis for that request identity. Findings are historical, held in memory, and not revalidated. Restart the application to begin a fresh run. Capacity shutdown also requires a fresh run; toggling analysis or increasing limits does not reset it.

## Limits and privacy

Buffering, copying, JSON normalization, hashing, and logging add allocation and timing overhead. Prefer finite responses; streaming, server-sent events, large downloads, and explicit response-body features require compatibility checks. The response-size limit is not a total memory limit or a QUERY buffering limit.

History retention and capacity are best effort under concurrency. Analysis uses concurrent collections and point-in-time snapshots without application-level locking; those collections may synchronize internally.

Built-in analysis has no outbound transport. Logging providers and custom sinks may export data; full-request logging can disclose sensitive values. Fingerprints are not encryption or guaranteed anonymization, and temporary QUERY buffering files may contain request data.

See [analysis details](docs/analysis-reference.md) for JSON fallback rules, logging setup, metrics, custom reporting, and compatibility limits. Runnable examples are in [samples](samples/README.md), including the [local news simulation](samples/MinimalApiWithCacheDetection/README.md). The .NET Framework sample uses exact-byte comparison; outbound HTTP clients and internal service calls are outside the analyzer's scope.
