using CacheAnalyzer.AspNetCore.Extensions;
using CacheAnalyzer.Core.Options;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://localhost:5080");
builder.Services.AddControllers();
builder.Services.AddCacheAnalyzer(options =>
{
    options.MinimumObservationsPerKey = 3;
    options.MinimumPayloadToFingerprintRatio = 10;
    options.MaximumDistinctRequests = 200;
    options.MaximumObservationsPerKey = 1_000;
});
builder.Services.Configure<CacheAnalysisOptions>(builder.Configuration.GetSection("CacheAnalyzer"));

var app = builder.Build();
app.UseRouting();
app.UseCacheAnalyzer();
app.MapControllers();
app.MapGet("/", () => new { sample = "ASP.NET Core", endpoints = new[] { "GET /api/products", "QUERY /api/products/search", "GET /api/live", "GET /health" } });
app.MapGet("/health", () => Results.Ok(new { status = "ready" }));
app.Run();
