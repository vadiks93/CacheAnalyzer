namespace CacheAnalyzer.Core;

public interface IResponseFingerprintProvider
{
    string CreateFingerprint(byte[] responseBody);
}
