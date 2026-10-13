using CacheAnalyzer.Sample.NetFramework.Models;
using System;
using System.Linq;
using System.Web.Http;

namespace CacheAnalyzer.Sample.NetFramework.Controllers
{
    public sealed class ProductsController : ApiController
    {
        private static readonly Product[] Products =
        {
            new Product { Id = 1, Name = "Keyboard", Price = 75 },
            new Product { Id = 2, Name = "Mouse", Price = 30 },
            new Product { Id = 3, Name = "Monitor", Price = 250 }
        };

        [HttpGet, Route("api/products")]
        public IHttpActionResult Get() => Ok(Products);

        // Return an empty response explicitly: the legacy host can otherwise serialize
        // its default 405 error body for HEAD. This method still bypasses analysis.
        [AcceptVerbs("HEAD"), Route("api/products")]
        public IHttpActionResult Head() => StatusCode(System.Net.HttpStatusCode.MethodNotAllowed);

        [AcceptVerbs("QUERY"), Route("api/products/search")]
        public IHttpActionResult Search([FromBody] ProductQuery query)
        {
            if (query == null || !ModelState.IsValid) return BadRequest("Supply a JSON body with maxPrice.");
            return Ok(Products.Where(p => p.Price <= query.MaxPrice));
        }

        [HttpGet, Route("api/live")]
        public IHttpActionResult Live() => Ok(new { value = Guid.NewGuid() });

        [HttpGet, Route("health")]
        public IHttpActionResult Health() => Ok(new { status = "ready" });
    }

}
