namespace CacheAnalyzer.Core;

public interface IResponseAnalysisSink
{
    void Report(ResponseAnalysis analysis);
}
