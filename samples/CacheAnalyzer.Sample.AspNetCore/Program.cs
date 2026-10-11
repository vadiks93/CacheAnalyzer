using CacheAnalyzer.AspNetCore;
using CacheAnalyzer.Core;
using CacheAnalyzerHelper;
using Microsoft.AspNetCore.Mvc;

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

[ApiController]
public sealed class ProductsController : ControllerBase
{
    [HttpGet("/api/products")]
    [CacheAnalyze]
    public IActionResult Get() => Ok(Products);

    [AcceptVerbs("QUERY", Route = "/api/products/search")]
    [CacheAnalyze]
    public IActionResult Search([FromBody] ProductQuery query) =>
        Ok(Products.Where(p => p.Price <= query.MaxPrice));

    [HttpGet("/api/live")]
    [CacheAnalyze]
    public IActionResult Live() => Ok(new { value = Guid.NewGuid() });

    private static readonly Product[] Products =
    [
        new(1, "Keyboard", 75),
        new(2, "Mouse", 30),
        new(3, "Monitor", 250)
    ];
}

public sealed record Product(int Id, string Name, decimal Price);
public sealed class ProductQuery
{
    public decimal MaxPrice { get; set; } = decimal.MaxValue;
}
