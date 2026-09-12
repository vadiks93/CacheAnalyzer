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
            $bytes = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $hash = [Convert]::ToBase64String($sha.ComputeHash($bytes)) } finally { $sha.Dispose() }
            [PSCustomObject]@{
                Status = [int]$response.StatusCode
                ETag = [string]$response.Headers.ETag
                Body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                Length = $bytes.Length
                Hash = $hash
                ContentType = $response.Content.Headers.ContentType.MediaType
                Disposition = [string]$response.Content.Headers.ContentDisposition
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
        foreach ($scenario in @(
            @{ Name = 'large-json'; Type = 'application/json'; Minimum = 300000 },
            @{ Name = 'text'; Type = 'text/plain'; Minimum = 131072 },
            @{ Name = 'xml'; Type = 'application/xml'; Minimum = 50000 },
            @{ Name = 'csv-file'; Type = 'text/csv'; Minimum = 50 },
            @{ Name = 'binary-file'; Type = 'application/octet-stream'; Minimum = 262144 },
            @{ Name = 'oversized-file'; Type = 'application/octet-stream'; Minimum = 4194304 }
        )) {
            $url = "$base/api/scenarios/" + $scenario.Name
            $initial = Send-SampleRequest GET $url
            Assert-Sample ($initial.Status -eq 200 -and -not $initial.ETag) "$url must return 200 without ETag"
            Assert-Sample ($initial.ContentType -eq $scenario.Type -and $initial.Length -ge $scenario.Minimum) "$url content type or size is incorrect"
            if ($scenario.Name.EndsWith('-file')) {
                Assert-Sample ($initial.Disposition -match 'attachment') "$url must remain a file attachment"
            }
            if ($scenario.Name -eq 'large-json') {
                $catalog = $initial.Body | ConvertFrom-Json
                Assert-Sample ($catalog.products.Count -eq 2000) "$url must contain 2000 products"
            }
            if ($scenario.Name -eq 'xml') {
                $catalogXml = [xml]$initial.Body
                Assert-Sample ($catalogXml.catalog.product.Count -eq 1000) "$url must contain 1000 XML products"
            }
            1..2 | ForEach-Object {
                $again = Send-SampleRequest GET $url -ETag '*'
                Assert-Sample ($again.Status -eq 200 -and $again.Hash -eq $initial.Hash -and -not $again.ETag) "$url must preserve every byte on repeat requests"
            }
        }
        $random = Send-SampleRequest GET "$base/api/scenarios/random-json"
        $randomAgain = Send-SampleRequest GET "$base/api/scenarios/random-json"
        Assert-Sample ($random.Status -eq 200 -and $randomAgain.Status -eq 200 -and $random.Hash -ne $randomAgain.Hash) "$base random payload must change"
        $versions = @(1..6 | ForEach-Object {
            $mixed = Send-SampleRequest GET "$base/api/scenarios/mixed-json"
            Assert-Sample ($mixed.Status -eq 200 -and -not $mixed.ETag) "$base mixed payload must return 200 without ETag"
            ($mixed.Body | ConvertFrom-Json).version
        })
        Assert-Sample (@($versions | Where-Object { $_ -eq 'A' }).Count -eq 3 -and @($versions | Where-Object { $_ -eq 'B' }).Count -eq 3) "$base mixed sequence must contain three of each version (run without concurrent callers)"
        Write-Host "PASS $base - GET, QUERY, conditional responses, changed content, health, HEAD"
        Write-Host "PASS $base - large/random/mixed JSON, text, XML, CSV and binary attachments, oversized response"
    }
} finally { $client.Dispose() }
