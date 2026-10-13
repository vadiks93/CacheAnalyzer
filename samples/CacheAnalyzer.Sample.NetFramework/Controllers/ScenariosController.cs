using CacheAnalyzer.Samples.Shared.Services;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Web.Http;

namespace CacheAnalyzer.Sample.NetFramework.Controllers
{
    public sealed class ScenariosController : ApiController
    {
        [HttpGet, Route("api/scenarios/{name}")]
        public IHttpActionResult Get(string name)
        {
            var payload = SamplePayloads.Create(name);
            if (payload == null) return NotFound();
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload.Bytes)
            };
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(payload.ContentType);
            if (payload.FileName != null)
                response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
                {
                    FileName = payload.FileName
                };
            return ResponseMessage(response);
        }
    }
}
