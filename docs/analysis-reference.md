# Analysis reference

See the [README](../README.md) for installation and setup.

## JSON comparison details

The same attribute option works on controller actions. Paths are case-sensitive JSON Pointers: `/request_id` selects the root property; `/results/0/id` selects a field in the first array item. Escape `/` in property names as `~1` and `~` as `~0`. Paths select exact locations, with no wildcards; missing paths are ignored. An empty path (the whole document) is unsupported. Selecting an object/array omits its subtree; selecting an array item omits that item using its original index.

With a nonempty exclusion list, UTF-8 `application/json` and `+json` responses are parsed, object properties sorted ordinally, and insignificant whitespace removed before hashing. Array order and JSON value types remain significant; numeric spellings such as `1` and `1.0` are not unified. The original response bytes, headers, and status remain untouched. This adds parsing, sorting, and allocation overhead within the existing capture-size and fingerprint-duration limits; parsing depth is limited to 64.

Non-JSON, malformed or overly deep JSON, unsupported charsets/content encodings, invalid pointers, and duplicate object names encountered during normalization fall back to complete-byte hashing. `ResponseAnalysis.FingerprintComparison` and the corresponding log field report `ExactBytes`, `JsonExclusionsApplied`, or `JsonExclusionsNotApplied` (exact fallback). Applied and fallback observations do not compare as equal. UTF-8 BOM-prefixed JSON also falls back to exact bytes.

Matching normalized observations produce **StableDataCandidate**, meaning only the included data was stable. They do not establish that the complete response can be replayed; request metadata may need regenerating. These findings are retained in the candidate store with the same capacity and historical-finding behavior as ordinary candidates, but log `cacheable=partial`. No exclusions are inferred from property names. The .NET Framework sample does not apply this option.

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
| `MaximumFingerprintDuration`            | 80 ms   | Exclude requests whose completed fingerprint generation exceeds this duration.                       |
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
| `StableDataCandidate` | `partial` | Included JSON data matches after explicit exclusions; the complete response may vary. |
| `CacheCandidate`                           | `1`         | Enough retained responses match and the payload-to-fingerprint ratio qualifies. |
| `ResponseChanged`                          | `0`         | Response bytes differ.                                                          |
| `PayloadTooLarge` / `FingerprintTooSlow`   | `0`         | Analysis exceeded the configured size or duration limit.                        |
| `FirstObservation` / `InsufficientTraffic` | `unknown`   | More observations are needed.                                                   |

Candidate (including stable-data), oversized, and slow-fingerprint events use Information; changed and pending findings use Debug. These values describe analysis results, not HTTP caching directives. Ignored responses produce no analysis findings.

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

Analysis logs include `FingerprintBytes` and `PayloadToFingerprintRatio` (original payload bytes divided by stored fingerprint size), matching the built-in history ratio check. The size counts the encoded fingerprint representation, including its internal comparison-mode prefix when JSON exclusions are configured; it is not the raw 32-byte SHA-256 digest. Both values are null when no fingerprint was generated. Logging these metrics does not enable fingerprint-value disclosure.

Prefer ordinary, finite responses with small payloads. Response forwarding preserves content, but capturing copies, buffering QUERY bodies, hashing, and reporting add allocation and timing overhead. `MaximumPayloadBytes` bounds the ASP.NET Core response capture per request; it is not a total memory limit or a QUERY buffering limit. The .NET Framework sample checks response size after buffering.

Avoid indefinite streaming, server-sent events, large downloads, and endpoints requiring prompt flushed delivery. Fingerprinting requires a completed response and can delay request completion. Explicit response start/completion, file sending, custom response-body features, and middleware ordering may require compatibility checks in your pipeline. Synchronous writes remain subject to the server's synchronous-I/O policy.

Comparison is byte-for-byte by default. Timestamps, random IDs, and randomized payload encryption can change fingerprints even when business data is stable. Explicit JSON exclusions enable the normalized comparison described above. Randomized QUERY bodies also create separate identities. The analyzer does not decrypt payloads; HTTPS transport encryption does not affect this comparison.

Built-in analysis has no outbound transport. Fingerprints and history stay in the host process unless logging providers or custom services export them. SHA-256 fingerprints are not encryption or guaranteed anonymization. Full request logging can disclose sensitive query values; route templates can also contain sensitive literals. Dumps, paging, and temporary QUERY buffering files may contain analysis/request data; clearing buffers does not securely erase it.
