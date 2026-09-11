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

## Integration differences

The .NET 10 sample uses the real `AddCacheAnalyzer`, `UseCacheAnalyzer`, and `[CacheAnalyze]` integration. Its `Program.cs` contains the registration and controller examples.

The .NET Framework sample references `CacheAnalyzer.Core` (`netstandard2.0`). The existing ASP.NET Core middleware and Metalama marker target .NET 10 and cannot be referenced from .NET Framework. `SampleCacheAnalysisHandler` demonstrates calling the shared fingerprint provider and history from a Web API message handler, selecting only GET/QUERY requests under `/api/`. It is sample code for small buffered responses, not a supported general-purpose Framework adapter or full equivalent of the ASP.NET Core integration. It does not expose the runtime switch, attribute selection, or pluggable reporting sinks.

Both examples use public, fixed demo data and local HTTP addresses. QUERY request content is incorporated into the analysis key without logging its raw body.
