using System;
using System.Threading;
using Microsoft.Owin.Hosting;

namespace CacheAnalyzer.Sample.NetFramework
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            var address = args.Length > 0 ? args[0] : "http://localhost:5081/";
            using (WebApp.Start<Startup>(address))
            using (var stopped = new ManualResetEventSlim())
            {
                Console.WriteLine(".NET Framework sample listening at " + address);
                Console.WriteLine("Press Ctrl+C to stop.");
                Console.CancelKeyPress += (sender, e) => { e.Cancel = true; stopped.Set(); };
                stopped.Wait();
            }
        }
    }

}
