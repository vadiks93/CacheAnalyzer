using System.Security.Cryptography;
using System.Text;

namespace CacheAnalyzer.Core.Services.Fingerprinting;

/// <summary>Opaque identity for a complete request key; not encryption or anonymization.</summary>
public static class RequestIdentity
{
    public static string Create(string requestKey)
    {
        if (requestKey is null) throw new ArgumentNullException(nameof(requestKey));
        using var sha = SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(requestKey)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
