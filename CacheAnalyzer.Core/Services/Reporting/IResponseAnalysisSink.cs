using CacheAnalyzer.Core.Models;
namespace CacheAnalyzer.Core.Services.Reporting;

public interface IResponseAnalysisSink
{
    void Report(ResponseAnalysis analysis);
}
