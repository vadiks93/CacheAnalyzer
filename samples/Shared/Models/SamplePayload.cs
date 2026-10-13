#nullable enable
namespace CacheAnalyzer.Samples.Shared.Models
{
    public sealed class SamplePayload
    {
        public SamplePayload(byte[] bytes, string contentType, string? fileName)
        {
            Bytes = bytes;
            ContentType = contentType;
            FileName = fileName;
        }

        public byte[] Bytes { get; }
        public string ContentType { get; }
        public string? FileName { get; }
    }
}
