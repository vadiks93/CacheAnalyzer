# News timing check

The `/news` endpoint and ten simulation endpoints `/news/1` through `/news/10` return the supplied APITube response from `Fixtures/news.json`, loaded once at startup and returned as JSON with HTTP 200. It makes no upstream HTTP requests and consumes no API quota. The payload, including `request_id`, stays fixed across requests. Restart the app after changing the fixture.

Start the Minimal API, then run in PowerShell:

```powershell
.\samples\MinimalApiWithCacheDetection\Measure-News.ps1 -Label analyzer-on
```

If your PowerShell execution policy blocks unsigned local scripts, run this script in a separate process with a process-only policy override:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\samples\MinimalApiWithCacheDetection\Measure-News.ps1 -Label analyzer-on
```

The script sends 100 sequential requests, randomly choosing from `https://localhost:7168/news/1` through `/news/10` for each request, waiting 100 ms after each completed request before starting the next. It measures full-response latency, including the body, with one reused HTTP client. The CSV and latency analysis include completed HTTP 200 responses only. HTTP errors (including upstream rate limits surfaced by the app as HTTP 500) and timeouts are excluded; they remain visible in console output and the JSON failure count. Failed attempts are not retried, so `Count` is the number of attempts, not a target number of successes. An all-failure run writes a header-only CSV and null latency statistics, then reports an error. CSV request timings and a JSON summary are saved under the project's ignored `TestResults` directory. Response bodies are not saved.

For comparison, stop the app, disable both `AddCacheAnalyzer(...)` and `UseCacheAnalyzer()` in Program.cs, rebuild/restart, and run:

```powershell
.\samples\MinimalApiWithCacheDetection\Measure-News.ps1 -Label analyzer-off
```

Use Release without a debugger and the same request settings for both runs. HTTPS requires a trusted local development certificate. The label only names the run; it does not enable or disable analysis.

Each path has its own analyzer history even though the response bytes are identical. Random selection can be uneven; after a path becomes a candidate, subsequent calls to that path still bypass analysis. Ten paths extend the simulation but do not guarantee every request is analyzed. The CSV records the selected URL, and JSON includes target URLs and per-path attempt/success/failure counts.

`-RandomSeed 42` (the default) repeats the URL selection sequence for comparable on/off runs with identical settings, including warmups. `-EndpointCount 1` calls the exact `-Url` (default `/news`) instead; values 2?10 append `/1` through `/N`.

Optional parameters include `-Count 100`, `-IntervalMilliseconds 100`, `-WarmupCount 5`, `-TimeoutSeconds 60`, `-Url`, and `-OutputDirectory`. Warmup defaults to zero so the initial request remains visible; configured warmups still affect analyzer history.

These are local end-to-end HTTP timings, including response writing, transfer, and any enabled analyzer work. They exclude upstream API latency and per-request fixture disk reads. CacheAnalyzer stops analyzing an identity after recording a candidate, so later requests in that run may bypass analysis. These results do not isolate fingerprint CPU cost. The fixed fixture provides stable data for candidate detection. Restart the app between comparisons to reset retained analyzer state.

VS Code includes a `.NET: Release sample Minimal API (no debugger)` launch configuration. Select it in Run and Debug and start it with F5 or Ctrl+F5. It builds Release, runs without attaching the debugger, and sets both `DOTNET_ENVIRONMENT` and `ASPNETCORE_ENVIRONMENT` to `Production`, while keeping the local HTTP/HTTPS addresses used by this script. Production environment settings skip `appsettings.Development.json` and the sample's debugger-only timing override. Local hosting, development HTTPS certificates, configuration, and upstream network conditions still differ from a deployed production service.

Each sample and the test executable have Debug and Release launch entries backed by matching build tasks. Existing Debug entries remain available. `launch.json` supports running as well as debugging: build configuration, runtime environment, and debugger attachment are independent settings.
