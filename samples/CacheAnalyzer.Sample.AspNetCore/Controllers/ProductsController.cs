using CacheAnalyzer.Sample.AspNetCore.Models;
using CacheAnalyzerHelper.Attributes;
using Microsoft.AspNetCore.Mvc;

namespace CacheAnalyzer.Sample.AspNetCore.Controllers;

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

