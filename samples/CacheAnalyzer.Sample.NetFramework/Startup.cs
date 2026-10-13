using CacheAnalyzer.Sample.NetFramework.Handlers;
using System.Web.Http;
using Owin;

namespace CacheAnalyzer.Sample.NetFramework
{
    public sealed class Startup
    {
        public void Configuration(IAppBuilder app)
        {
            var config = new HttpConfiguration();
            config.MapHttpAttributeRoutes();
            config.MessageHandlers.Add(new SampleCacheAnalysisHandler());
            app.UseWebApi(config);
        }
    }
}
