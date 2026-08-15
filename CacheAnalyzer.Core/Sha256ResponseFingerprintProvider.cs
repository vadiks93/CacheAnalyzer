using System.Security.Cryptography;

namespace CacheAnalyzer.Core;

public sealed class Sha256ResponseFingerprintProvider : IResponseFingerprintProvider
{
    public string CreateFingerprint(byte[] responseBody)
    {
        if (responseBody is null)
        {
            throw new ArgumentNullException(nameof(responseBody));
        }

        using (var algorithm = SHA256.Create())
        {
            var hash = algorithm.ComputeHash(responseBody);
            var value = Convert.ToBase64String(hash)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

            return $"\"{value}\"";
        }
    }
}
