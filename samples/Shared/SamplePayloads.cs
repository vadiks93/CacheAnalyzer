#nullable enable
using System;
using System.IO;
using System.Text;
using System.Threading;

namespace CacheAnalyzer.Samples
{
    // Linked into both sample hosts so the same scenarios produce the same bytes.
    public static class SamplePayloads
    {
        private static int mixedRequests;

        public static SamplePayload? Create(string name)
        {
            switch (name)
            {
                case "large-json":
                    var json = new StringBuilder("{\"products\":[");
                    for (var i = 0; i < 2000; i++)
                    {
                        if (i > 0) json.Append(',');
                        json.Append("{\"id\":").Append(i.ToString(System.Globalization.CultureInfo.InvariantCulture))
                            .Append(",\"description\":\"").Append(new string('x', 128)).Append("\"}");
                    }
                    return Text(json.Append("]}").ToString(), "application/json");
                case "random-json":
                    return Text("{\"sampleId\":\"" + Guid.NewGuid().ToString("N") +
                        "\",\"data\":\"" + new string('r', 64 * 1024) + "\"}", "application/json");
                case "mixed-json":
                    // A,A,A,B,B,B,... at the SAME URL. No changing query parameter.
                    var sequence = unchecked((uint)Interlocked.Increment(ref mixedRequests) - 1);
                    var version = (sequence / 3) % 2 == 0 ? "A" : "B";
                    return Text("{\"version\":\"" + version + "\",\"data\":\"" +
                        new string('m', 16 * 1024) + "\"}", "application/json");
                case "text":
                    return Text(new string('t', 128 * 1024) + "\n", "text/plain");
                case "xml":
                    var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><catalog>");
                    for (var i = 0; i < 1000; i++) xml.Append("<product><name>Keyboard</name><price>75</price></product>");
                    return Text(xml.Append("</catalog>").ToString(), "application/xml");
                case "csv-file":
                    return new SamplePayload(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
                        "Assets", "report.csv")), "text/csv; charset=utf-8", "report.csv");
                case "binary-file":
                    var binary = new byte[256 * 1024];
                    for (var i = 0; i < binary.Length; i++) binary[i] = (byte)(i % 256);
                    return new SamplePayload(binary, "application/octet-stream", "sample.bin");
                case "oversized-file":
                    return new SamplePayload(new byte[4 * 1024 * 1024], "application/octet-stream", "oversized.bin");
                default:
                    return null;
            }
        }

        private static SamplePayload Text(string value, string mediaType) =>
            new SamplePayload(Encoding.UTF8.GetBytes(value), mediaType + "; charset=utf-8", null);
    }

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
