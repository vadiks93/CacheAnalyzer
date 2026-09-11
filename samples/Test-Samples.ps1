param([string[]]$BaseUrls = @('http://localhost:5080', 'http://localhost:5081'))

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$client = New-Object System.Net.Http.HttpClient

function Send-SampleRequest([string]$Method, [string]$Url, [string]$Body = '', [string]$ETag = '') {
    $request = New-Object System.Net.Http.HttpRequestMessage ([System.Net.Http.HttpMethod]::new($Method)), $Url
    try {
        if ($Body) { $request.Content = [System.Net.Http.StringContent]::new($Body, [Text.Encoding]::UTF8, 'application/json') }
        if ($ETag) { $request.Headers.TryAddWithoutValidation('If-None-Match', $ETag) | Out-Null }
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        try {
            [PSCustomObject]@{
                Status = [int]$response.StatusCode
                ETag = [string]$response.Headers.ETag
                Body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            }
        } finally { $response.Dispose() }
    } finally { $request.Dispose() }
}

function Assert-Sample([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

try {
    foreach ($base in $BaseUrls) {
        $first = Send-SampleRequest GET "$base/api/products"
        Assert-Sample ($first.Status -eq 200 -and -not $first.ETag) "$base GET must return 200 without an ETag"
        $products = $first.Body | ConvertFrom-Json
        Assert-Sample ($products.Count -eq 3) "$base must return three products"
        $cached = Send-SampleRequest GET "$base/api/products" -ETag '*'
        Assert-Sample ($cached.Status -eq 200 -and $cached.Body -eq $first.Body -and -not $cached.ETag) "$base conditional GET must preserve the full body"
        $query = Send-SampleRequest QUERY "$base/api/products/search" '{"maxPrice":100}'
        Assert-Sample ($query.Status -eq 200 -and -not $query.ETag) "$base QUERY must return 200 without an ETag"
        $queryProducts = $query.Body | ConvertFrom-Json
        Assert-Sample ($queryProducts.Count -eq 2) "$base QUERY body must reach endpoint"
        $queryCached = Send-SampleRequest QUERY "$base/api/products/search" '{"maxPrice":100}' '*'
        Assert-Sample ($queryCached.Status -eq 200 -and $queryCached.Body -eq $query.Body -and -not $queryCached.ETag) "$base conditional QUERY must preserve the full body"
        $different = Send-SampleRequest QUERY "$base/api/products/search" '{"maxPrice":300}' '*'
        Assert-Sample ($different.Status -eq 200 -and $different.Body -ne $query.Body -and -not $different.ETag) "$base changed QUERY must return new representation"
        $live = Send-SampleRequest GET "$base/api/live"
        $changed = Send-SampleRequest GET "$base/api/live" -ETag '*'
        Assert-Sample ($changed.Status -eq 200 -and $changed.Body -ne $live.Body -and -not $changed.ETag) "$base live response must change"
        $health = Send-SampleRequest GET "$base/health"
        Assert-Sample ($health.Status -eq 200 -and -not $health.ETag) "$base health must bypass analysis"
        $head = Send-SampleRequest HEAD "$base/api/products"
        Assert-Sample (-not $head.ETag) "$base HEAD must bypass analysis"
        Write-Host "PASS $base - GET, QUERY, conditional responses, changed content, health, HEAD"
    }
} finally { $client.Dispose() }
