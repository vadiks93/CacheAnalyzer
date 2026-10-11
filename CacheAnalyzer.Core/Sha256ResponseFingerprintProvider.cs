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
            // Compare complete response bytes without parsing or excluding fields.
            // Volatile timestamps/IDs and randomized ciphertext therefore change the hash,
            // even when the underlying business data is unchanged.
            var hash = algorithm.ComputeHash(responseBody);
            var value = Convert.ToBase64String(hash)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

            return $"\"{value}\"";
        }
    }
}
