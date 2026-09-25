using Microsoft.AspNetCore.Http;

namespace CacheAnalyzer.AspNetCore;

// Each write forwards its bytes to the original response stream before copying them
// into _responseBytesForAnalysis, subject to the size limit. Forwarding does not wait
// for the whole response or its fingerprint. The copy is never replayed to the client.
// If the declared or accumulated size exceeds the limit, discard the analysis copy
// and stop capturing; forwarding continues. ExceededLimit remembers that transition.
// Flush/FlushAsync reach the destination and leave the analysis copy intact, so an
// endpoint can write and flush initial bytes before producing the rest of its response.
// Analyzer setup (including QUERY request-body buffering/hashing) can delay the first
// response bytes. Copying after writes adds overhead and can delay a subsequent flush
// or write; forwarding does not guarantee immediate delivery to the client.
// Response fingerprinting runs after the downstream pipeline finishes and can delay
// request completion. An indefinite response cannot be fingerprinted until it finishes.
// Explicit response start/completion, file sending, and custom response body features
// have not been validated with this wrapper.
// Per-response state: like the underlying response stream, writes must not overlap.
internal sealed class LimitedResponseBuffer(Stream destination, HttpResponse response, long limit) : Stream
{
    private readonly MemoryStream _responseBytesForAnalysis = new();
    private readonly long _limit = Math.Clamp(limit, 0, Array.MaxLength);
    public bool ExceededLimit { get; private set; }
    public long BytesWritten { get; private set; }
    public byte[] ToArray() => _responseBytesForAnalysis.ToArray();

    private bool WouldExceedCaptureLimit(int count) => ExceededLimit ||
        response.ContentLength > _limit || count > _limit - _responseBytesForAnalysis.Length;

    private void Reserve(int count)
    {
        var required = _responseBytesForAnalysis.Length + count;
        if (required > _responseBytesForAnalysis.Capacity)
            _responseBytesForAnalysis.Capacity = (int)Math.Min(_limit, Math.Max(required, Math.Max(256L, _responseBytesForAnalysis.Capacity * 2L)));
    }

    private void CaptureForAnalysis(ReadOnlySpan<byte> incomingChunk)
    {
        if (ExceededLimit) return;
        if (WouldExceedCaptureLimit(incomingChunk.Length))
        {
            _responseBytesForAnalysis.SetLength(0);
            _responseBytesForAnalysis.Capacity = 0;
            ExceededLimit = true;
            return;
        }

        Reserve(incomingChunk.Length);
        _responseBytesForAnalysis.Write(incomingChunk);
    }

    public override void Write(byte[] incomingChunk, int offset, int count) => Write(incomingChunk.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> incomingChunk)
    {
        destination.Write(incomingChunk);
        BytesWritten += incomingChunk.Length;
        CaptureForAnalysis(incomingChunk);
    }

    public override Task WriteAsync(byte[] incomingChunk, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(incomingChunk.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> incomingChunk, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await destination.WriteAsync(incomingChunk, cancellationToken);
        BytesWritten += incomingChunk.Length;
        CaptureForAnalysis(incomingChunk.Span);
    }

    public override void Flush() => destination.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => destination.FlushAsync(cancellationToken);
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) _responseBytesForAnalysis.Dispose();
        base.Dispose(disposing); // The server owns destination.
    }
}
