using CacheAnalyzer.Samples;
using CacheAnalyzerHelper;
using Microsoft.AspNetCore.Mvc;

[ApiController]
public sealed class ScenariosController : ControllerBase
{
    [HttpGet("/api/scenarios/{name}")]
    [CacheAnalyze]
    public IActionResult Get(string name)
    {
        var payload = SamplePayloads.Create(name);
        if (payload == null) return NotFound();
        // Byte-backed file results deliberately avoid streaming and automatic file ETags.
        return payload.FileName == null
            ? File(payload.Bytes, payload.ContentType)
            : File(payload.Bytes, payload.ContentType, payload.FileName);
    }
}
