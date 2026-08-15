# CacheAnalyzer

CacheAnalyzer separates response analysis from framework-specific HTTP behavior.

## Scope

The initial version supports inbound ASP.NET Core requests handled by:

- MVC and API controllers
- Minimal API endpoints

It analyzes newly generated responses produced by these server endpoints, reports caching opportunities, emits ETags when analysis is suitable, and handles conditional `304 Not Modified` responses. It does not store endpoint response bodies or use ASP.NET Core Output Caching.

Outbound `HttpClient`, typed API-client, and internal service-call analysis are not part of the initial scope. These integrations may be considered later without changing the core response-analysis contracts.

## Projects

- `CacheAnalyzer.Core` targets `netstandard2.0` and owns response fingerprinting.
- `CacheAnalyzerHelper` contains the Metalama `[CacheAnalyzer]` method marker.
- `CacheAnalyzer.AspNetCore` translates matching HTTP ETags into `304 Not Modified`.

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
    options.MaximumTrackedRequests = 1_000;
    options.MinimumObservations = 10;
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

Use `EndpointSelectionMode.AttributedEndpoints` for one-by-one opt-in through `[CacheAnalyze]`, or `EndpointSelectionMode.AllGetAndHeadRequests` to analyze every GET and HEAD endpoint.

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

Every eligible tagged response returns an `ETag`; clients that do not support validation can ignore it. The first request returns `200 OK` with both the response payload and the ETag header. When a later request sends that value in `If-None-Match`, CacheAnalyzer executes the endpoint again and hashes the newly generated response. If it is unchanged, CacheAnalyzer returns `304` without a response body; otherwise it returns `200 OK` with the new payload and ETag.

Analysis runs even when the request does not contain `If-None-Match`. A response becomes a cache candidate only when the same request produces enough identical responses inside the configured observation window and its payload is sufficiently larger than its ETag. Large payloads are not hashed, and responses whose fingerprint takes longer than the configured threshold do not receive an ETag.

The in-memory tracker has a fixed capacity. Once full, it continues updating known request keys but does not admit new keys. A rejected key is reported with `TrackingCapacityReached = true` while its cache recommendation remains `InsufficientTraffic`. The tracker stores append-only fingerprint observations inside the configured window, never response bodies, and does not lock request processing.

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

This implementation saves response bandwidth, but it deliberately does not cache backend endpoint responses or avoid endpoint execution.
