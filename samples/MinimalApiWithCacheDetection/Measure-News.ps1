<#
.SYNOPSIS
Measures sequential end-to-end HTTP response times and writes CSV/JSON results.
.EXAMPLE
.\Measure-News.ps1 -Label analyzer-on
.EXAMPLE
.\Measure-News.ps1 -Label analyzer-off -WarmupCount 5
#>
[CmdletBinding()]
param(
    [uri]$Url = 'https://localhost:7168/news',
    [ValidateRange(1, 10)][int]$EndpointCount = 10,
    [int]$RandomSeed = 42,
    [ValidateRange(1, 100000)][int]$Count = 120,
    [ValidateRange(0, 60000)][int]$IntervalMilliseconds = 100,
    [ValidateRange(0, 10000)][int]$WarmupCount = 0,
    [ValidateRange(1, 3600)][int]$TimeoutSeconds = 60,
    [ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$Label = 'news',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'TestResults')
)

$ErrorActionPreference = 'Stop'
if ($Url.Scheme -notin @('http', 'https')) { throw 'Url must use HTTP or HTTPS.' }
# A single endpoint preserves the exact supplied URL. Otherwise append /1 .. /N
# to its path while retaining its query string (if any).
$targetUrls = @(for ($endpoint = 1; $endpoint -le $EndpointCount; $endpoint++) {
        $target = [System.UriBuilder]::new($Url)
        if ($EndpointCount -gt 1) { $target.Path = $target.Path.TrimEnd('/') + "/$endpoint" }
        $target.Uri.AbsoluteUri
    })
$random = [System.Random]::new($RandomSeed)
Add-Type -AssemblyName System.Net.Http

function Get-Percentile([double[]]$Sorted, [double]$Percentile) {
    if ($Sorted.Count -eq 0) { return $null }
    return [math]::Round($Sorted[[math]::Max(0, [int][math]::Ceiling($Percentile * $Sorted.Count) - 1)], 3)
}

$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)
$client.DefaultRequestHeaders.Accept.ParseAdd('application/json')
$rows = [System.Collections.Generic.List[object]]::new()
$startedAt = [DateTimeOffset]::Now
$runTimer = [System.Diagnostics.Stopwatch]::StartNew()
try {
    $total = $WarmupCount + $Count
    for ($index = 1; $index -le $total; $index++) {
        $response = $null
        $status = $null
        $size = $null
        $errorText = $null
        $requestUrl = $targetUrls[$random.Next($targetUrls.Count)]
        $timestamp = [DateTimeOffset]::Now.ToString('o')
        $timer = [System.Diagnostics.Stopwatch]::StartNew()
        try {
            # ResponseContentRead includes the full body in elapsed time. Reuse
            # one client for connection pooling; never retain response payloads.
            $response = $client.GetAsync([uri]$requestUrl, [System.Net.Http.HttpCompletionOption]::ResponseContentRead).GetAwaiter().GetResult()
            $bytes = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
            $timer.Stop()
            $status = [int]$response.StatusCode
            $size = $bytes.LongLength
            if ($status -ne 200) { $errorText = "HTTP $status $($response.ReasonPhrase)" }
        }
        catch {
            $timer.Stop()
            $errorText = $_.Exception.GetBaseException().Message
        }
        finally {
            if ($null -ne $response) { $response.Dispose() }
        }

        $isWarmup = $index -le $WarmupCount
        $requestNumber = $index - $WarmupCount
        if (-not $isWarmup) {
            $rows.Add([pscustomobject]@{
                    Request             = $requestNumber
                    Url                 = $requestUrl
                    StartedAt           = $timestamp
                    StatusCode          = $status
                    Success             = ($null -eq $errorText)
                    ElapsedMilliseconds = [math]::Round($timer.Elapsed.TotalMilliseconds, 3)
                    ResponseBytes       = $size
                    Error               = $errorText
                })
        }
        $phase = if ($isWarmup) { "Warmup $index/$WarmupCount" } else { "Request $requestNumber/$Count" }
        $result = if ($errorText) { $errorText } else { "HTTP $status, $size bytes" }
        Write-Host ('{0}: {1:N3} ms; {2}; {3}' -f $phase, $timer.Elapsed.TotalMilliseconds, $result, $requestUrl)
        # A pause after each completed request, not a fixed start-to-start rate.
        if ($index -lt $total -and $IntervalMilliseconds -gt 0) { Start-Sleep -Milliseconds $IntervalMilliseconds }
    }
}
finally {
    $runTimer.Stop()
    $client.Dispose()
}

$successful = @($rows | Where-Object { $_.StatusCode -eq 200 -and $_.Success })
$durations = @($successful | ForEach-Object ElapsedMilliseconds | Sort-Object)
$stats = $durations | Measure-Object -Average -Minimum -Maximum
$average = if ($successful.Count -gt 0) { [math]::Round($stats.Average, 3) } else { $null }
$summary = [pscustomobject]@{
    Label                = $Label
    Url                  = $Url.AbsoluteUri
    TargetUrls           = $targetUrls
    RandomSeed           = $RandomSeed
    EndpointRequests     = @($rows | Group-Object Url | ForEach-Object {
            [pscustomobject]@{
                Url                = $_.Name
                MeasuredRequests   = $_.Count
                SuccessfulRequests = @($_.Group | Where-Object { $_.StatusCode -eq 200 -and $_.Success }).Count
                FailedRequests     = @($_.Group | Where-Object { $_.StatusCode -ne 200 -or -not $_.Success }).Count
            }
        })
    StartedAt            = $startedAt.ToString('o')
    MeasuredRequests     = $Count
    WarmupRequests       = $WarmupCount
    IntervalMilliseconds = $IntervalMilliseconds
    SuccessfulRequests   = $successful.Count
    FailedRequests       = $Count - $successful.Count
    AverageMilliseconds  = $average
    MinimumMilliseconds  = $stats.Minimum
    MedianMilliseconds   = Get-Percentile $durations 0.50
    P95Milliseconds      = Get-Percentile $durations 0.95
    MaximumMilliseconds  = $stats.Maximum
    TotalRunSeconds      = [math]::Round($runTimer.Elapsed.TotalSeconds, 3)
    Notes                = 'Latency includes connection setup and full response body, excludes pauses. Statistics and CSV include completed HTTP 200 responses only. Failures are excluded and counted separately; failed attempts are not retried. Warmup is excluded from statistics but still reaches the app and its analyzer. Total run time includes warmup and pauses.'
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$runName = '{0}_{1}_{2}_with_caching' -f $Label, $startedAt.ToString('yyyy-MM-dd_HH-mm-ss-fff'), ([guid]::NewGuid().ToString('N').Substring(0, 6))
$csvPath = Join-Path $OutputDirectory ($runName + '.csv')
$jsonPath = Join-Path $OutputDirectory ($runName + '.json')
if ($successful.Count -gt 0) {
    $successful | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding UTF8
}
else {
    # Keep a valid, header-only CSV when every measured request failed.
    '"Request","Url","StartedAt","StatusCode","Success","ElapsedMilliseconds","ResponseBytes","Error"' | Set-Content -LiteralPath $csvPath -Encoding UTF8
}
$summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
$summary | Format-List | Out-Host
Write-Host "HTTP 200 per-request results: $csvPath"
Write-Host "Summary: $jsonPath"
if ($successful.Count -eq 0) { throw 'No HTTP 200 responses. CSV contains no data rows. See console output for failures and the JSON summary for counts; check API rate limits, the app, URL, and HTTPS development certificate trust.' }
