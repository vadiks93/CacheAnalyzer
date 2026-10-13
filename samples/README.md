# Try CacheAnalyzer

Two independent APIs are included in the main solution. Both serve a stable product list, accept a QUERY body, and generate a changing response at `/api/live`.

## Run

From the repository root, build with the .NET 10 SDK:

```powershell
dotnet build .\CacheAnalyzer.slnx
```

Run each API in its own terminal:

```powershell
dotnet run --no-build --project .\samples\CacheAnalyzer.Sample.AspNetCore
```

```powershell
.\samples\CacheAnalyzer.Sample.NetFramework\bin\Debug\net48\CacheAnalyzer.Sample.NetFramework.exe
```

The ASP.NET Core sample uses `http://localhost:5080`; the Framework sample uses `http://localhost:5081`. The Framework executable requires Windows and .NET Framework 4.8. Reference assemblies are restored through NuGet for compilation. It hosts Web API 2 with OWIN, without requiring an IIS site.

If the Framework host reports access denied when opening its URL, run this once from an administrator PowerShell terminal, substituting your Windows account if needed:

```powershell
netsh http add urlacl url=http://localhost:5081/ user="$env:USERDOMAIN\$env:USERNAME"
```

That reserves only the sample URL. To remove the reservation later, use `netsh http delete urlacl url=http://localhost:5081/` from an administrator terminal.

## Try requests

These commands use `curl.exe` to avoid the Windows PowerShell `curl` alias. Change the port to `5081` to try the Framework sample.

```powershell
curl.exe -i http://localhost:5080/api/products
curl.exe -i http://localhost:5080/api/products
curl.exe -i http://localhost:5080/api/products
```

Expect `200 OK`, the complete product list, and no generated ETag. After three identical requests, the console reports `CacheCandidate`. The sample lowers the observation and payload ratio thresholds to make this easy to see.

Fingerprints and recommendations appear only in server-side analysis. The `.http` file includes an If-None-Match request to demonstrate that the full response is still returned. CacheAnalyzer never creates a 304 response or changes an API-owned ETag.

```powershell
curl.exe -i -X QUERY http://localhost:5080/api/products/search -H "Content-Type: application/json" --data-binary "@samples/query.json"
curl.exe -i http://localhost:5080/api/live
curl.exe -i http://localhost:5080/health
```

QUERY returns products costing at most 100. Changing `maxPrice` creates a separate history entry. `/api/live` changes every time and reports changing content. `/health` bypasses analysis and has no generated ETag. HEAD and POST bypass analysis; endpoint routing may return `405` for these unsupported methods.

With both APIs running, execute the HTTP smoke checks:

```powershell
powershell -ExecutionPolicy Bypass -File .\samples\Test-Samples.ps1
```

## Payload scenarios in both APIs

`MinimumObservationsPerKey` is **per distinct request key**, not a total across endpoints. The library default is 100; these samples use 3. The latest retained observations within the window must all match before that key becomes a candidate (and the payload ratio must qualify). Different URLs, query strings, or QUERY bodies have separate histories.

All routes below use GET under `/api/scenarios/` on either port:

| Route suffix | Payload | Expected analysis when repeated |
| --- | --- | --- |
| `large-json` | 2,000 products, about 306 KiB | Stable; candidate after 3 observations |
| `random-json` | About 64 KiB of JSON with a new random GUID each time | `ResponseChanged` after the first observation |
| `mixed-json` | About 16 KiB of JSON; A,A,A,B,B,B repeating | First block becomes a candidate; then changes remain visible in history |
| `text` | 128 KiB of plain text plus newline | Stable; candidate after 3 observations |
| `xml` | 1,000 XML product elements | Stable; candidate after 3 observations |
| `csv-file` | Actual `Assets/report.csv` file, downloaded as an attachment | Stable; edit the output file to simulate a changed export |
| `binary-file` | 256 KiB of deterministic binary bytes, downloaded as `sample.bin` | Stable; candidate after 3 observations |
| `oversized-file` | 4 MiB download | `PayloadTooLarge` with the default 3 MiB hashing limit; full download still returned |

Recommendations assume fingerprinting finishes within the configured duration. Slow hashing is reported separately and excludes that exact request from subsequent response analysis. After 50 distinct slow GET query variants on the same origin/path, the path is excluded. The slow-key store retains 500 keys; another distinct slow key stops the analyzer, logs once, and clears retained analysis state. Both samples apply this policy, with approximate concurrent accounting. The oversized payload is still buffered: the hashing limit does not cap buffering memory.

Both adapters retain the first cache-candidate finding for each request key and skip later response analysis for that key. The default `MaximumCacheCandidates` is 200; exceeding it stops the run and clears observation history and slow exclusions while preserving candidate findings. Concurrent accounting is best effort. Findings describe the completed analysis run: restart with fresh analysis services after relevant data or application changes to reassess them. In ASP.NET Core, inspect the registered `CacheCandidateStore.GetSnapshot()`; the Framework sample exposes `SampleCacheAnalysisHandler.Candidates`.

The ASP.NET Core sample writes one JSON console log event per line, with separate analysis fields under `State`, including `Cacheable` (`"1"`, `"0"`, or `"unknown"`), recommendation, method/route-template `Endpoint`, hashed `fingerprintRequest`, payload size, fingerprint duration, and tracking-capacity flag. Full request details and fingerprints are omitted by default; independently enable `IncludeFullRequestKeyInLogs` or `IncludeFingerprintInLogs` globally or on an endpoint's `[CacheAnalyze]` attribute to include them. Debug is enabled for the analysis sink so pending findings are visible. Both adapters use hashed identities for retained history/candidates/slow exclusions. The Framework sample continues to use its sample Console.WriteLine reporting, shows hashed request IDs by default, and does not print fingerprints; its demo handler options can opt in to showing full keys.

The ASP.NET Core sample enables Debug logging for the analysis sink in `appsettings.json`, so first observations and changing responses appear alongside candidate reports. When launching its DLL directly, use the sample output directory as the working directory so that configuration is loaded. `dotnet run --project` uses the project directory automatically.

```powershell
1..3 | ForEach-Object { curl.exe -s -o NUL http://localhost:5080/api/scenarios/large-json }
1..6 | ForEach-Object { curl.exe -s -o NUL http://localhost:5080/api/scenarios/mixed-json }
curl.exe -i http://localhost:5080/api/scenarios/xml
curl.exe -o sample.bin http://localhost:5080/api/scenarios/binary-file
curl.exe -o report.csv http://localhost:5080/api/scenarios/csv-file
```

The mixed sequence is per host process and shared by callers; restart the host for a fresh sequence/history. A single differing observation prevents a candidate until it leaves the retained history. Do not vary the URL to simulate changing content: that would create different keys instead.

Formats other than JSON are useful because fingerprinting compares **exact bytes**. XML, CSV, text, and binary files work the same way; even whitespace changes or reordered JSON properties change the fingerprint. This is not semantic content comparison.

The CSV source in `Shared/Assets` is copied to each application's output `Assets` folder during build. File examples load or generate bounded byte arrays; they do not demonstrate streaming, range requests, or arbitrarily large file handling. `Shared/Services/SamplePayloads.cs` provides identical payload generation to both hosts.

## Integration differences

The .NET 10 sample uses the real `AddCacheAnalyzer`, `UseCacheAnalyzer`, and `[CacheAnalyze]` integration. Its `Program.cs` contains the registration and controller examples.

The .NET Framework sample references `CacheAnalyzer.Core` (`netstandard2.0`). The existing ASP.NET Core middleware and Metalama marker target .NET 10 and cannot be referenced from .NET Framework. `SampleCacheAnalysisHandler` demonstrates calling the shared fingerprint provider and history from a Web API message handler, selecting only GET/QUERY requests under `/api/`. It is sample code for small buffered responses, not a supported general-purpose Framework adapter or full equivalent of the ASP.NET Core integration. It does not expose the runtime switch, attribute selection, or pluggable reporting sinks.

Both examples use synthetic demo data and local HTTP addresses. QUERY request content is incorporated into the analysis key without logging its raw body.
