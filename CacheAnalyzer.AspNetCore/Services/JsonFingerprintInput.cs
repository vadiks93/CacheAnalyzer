using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace CacheAnalyzer.AspNetCore.Services;

internal static class JsonFingerprintInput
{
    // No shared mutable state. Parsing is bounded by capture size and JSON depth.
    public static bool TryNormalize(byte[] bytes, HttpResponse response, string[] paths, out byte[] normalized)
    {
        normalized = bytes;
        if (!MediaTypeHeaderValue.TryParse(response.ContentType, out var contentType)) return false;
        var media = contentType.MediaType;
        if (media is null || !(media.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
            media.EndsWith("+json", StringComparison.OrdinalIgnoreCase))) return false;
        var charset = contentType.CharSet?.Trim('"');
        if (!string.IsNullOrEmpty(charset) && !charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase)) return false;
        if (response.Headers.ContentEncoding.Any(value =>
            !string.IsNullOrEmpty(value) && !value.Equals("identity", StringComparison.OrdinalIgnoreCase))) return false;
        if (paths.Any(path => !IsPointer(path))) return false;

        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output))
                Write(document.RootElement, "", writer, new HashSet<string>(paths, StringComparer.Ordinal));
            normalized = output.ToArray();
            return true;
        }
        catch (JsonException) { return false; }
        // Escaped invalid Unicode may be accepted by the document reader but
        // rejected when a property name/value is decoded for normalization.
        catch (InvalidOperationException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private static bool IsPointer(string? path)
    {
        // Excluding the root would discard all data, so it is deliberately unsupported.
        if (string.IsNullOrEmpty(path) || path[0] != '/') return false;
        for (var i = 0; i < path.Length; i++)
            if (path[i] == '~' && (++i == path.Length || (path[i] != '0' && path[i] != '1'))) return false;
        return true;
    }

    private static string Escape(string name) => name.Replace("~", "~0").Replace("/", "~1");

    private static void Write(JsonElement element, string path, Utf8JsonWriter writer, HashSet<string> ignored)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
                // Duplicate names are ambiguous for consumers; use exact bytes instead.
                if (properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
                    throw new JsonException("Duplicate JSON property.");
                writer.WriteStartObject();
                foreach (var property in properties)
                {
                    var child = path + "/" + Escape(property.Name);
                    if (ignored.Contains(child)) continue;
                    writer.WritePropertyName(property.Name);
                    Write(property.Value, child, writer, ignored);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    var child = path + "/" + (index++).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (!ignored.Contains(child)) Write(item, child, writer, ignored);
                }
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
