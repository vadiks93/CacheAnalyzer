using CacheAnalyzer.AspNetCore.Extensions;
using CacheAnalyzerHelper.Attributes;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddCacheAnalyzer(options =>
{
    // Breakpoint pauses count toward fingerprint duration. Keep stepping from
    // triggering the slow-analysis guard while debugging this sample.
    if (builder.Environment.IsDevelopment() && System.Diagnostics.Debugger.IsAttached)
        options.MaximumFingerprintDuration = TimeSpan.MaxValue;
});

// Live API alternative: uncomment this registration and replace the mock routes below.
// Configure ApiTube:ApiKey through configuration or the ApiTube__ApiKey environment variable.
// builder.Services.AddHttpClient("ApiTubClient", client =>
// {
//     client.BaseAddress = new Uri("https://api.apitube.io/");
//     client.DefaultRequestHeaders.Add("X-API-Key", <your API key>);
// });

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseCacheAnalyzer();

// Load once at startup to keep disk I/O out of measured requests.
// This fixed fixture includes its original request_id and is never fetched upstream.
var newsPayload = await File.ReadAllBytesAsync(
    Path.Combine(app.Environment.ContentRootPath, "Fixtures", "news.json"));

app.MapGet("/news", [CacheAnalyze(IgnoreJsonPaths = new[] { "/request_id" })] () =>
    Results.Bytes(newsPayload, "application/json; charset=utf-8"))
.WithName("GetNews");

// Separate literal paths give each simulated request its own analyzer identity.
for (var index = 1; index <= 10; index++)
{
    app.MapGet($"/news/{index}", [CacheAnalyze(IgnoreJsonPaths = new[] { "/request_id" })] () =>
        Results.Bytes(newsPayload, "application/json; charset=utf-8"))
    .WithName($"GetNews{index}");
}

// Live API alternative (replace the mock /news mapping above; do not enable both):
// app.MapGet("/news", [CacheAnalyze(IgnoreJsonPaths = new[] { "/request_id" })] async (IHttpClientFactory factory, CancellationToken cancellationToken) =>
// {
//     var client = factory.CreateClient("ApiTubClient");
//     return await client.GetFromJsonAsync<MinimalApiWithCacheDetection.Models.NewsResponse>(
//         "v1/news/everything?title=Bitcoin&language.code=en&sort.by=published_at&per_page=10&page=1&organization.name=Apple%2CGoogle%2CMicrosoft",
//         cancellationToken);
// })
// .WithName("GetNews");

app.Run();



