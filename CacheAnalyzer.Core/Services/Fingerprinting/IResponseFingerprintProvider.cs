namespace CacheAnalyzer.Core.Services.Fingerprinting;

public interface IResponseFingerprintProvider
{
    string CreateFingerprint(byte[] responseBody);
}
