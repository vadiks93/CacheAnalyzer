using Microsoft.AspNetCore.Http;

namespace CacheAnalyzer.AspNetCore;

// This stream has two modes:
// 1. Collecting: append each incomingChunk (the bytes from one write) to
//    _responseBytesForAnalysis. After the downstream pipeline finishes, the middleware
//    uses the complete collected response to fingerprint it and forwards it to the original stream.
// 2. Forwarding: if Content-Length exceeds the limit or the next chunk would exceed it,
//    forward any collected bytes, release their storage, and set ExceededLimit once.
//    Every subsequent chunk goes directly to the original stream; fingerprinting is skipped.
// ExceededLimit remembers this transition even after the collection buffer is emptied.
// It can be set before that many bytes arrive when Content-Length already exceeds the limit.
// Buffering uses memory and delays delivery while collecting; it is not transparent to
// response timing or streaming. Headers, status codes, and payload bytes are preserved.
// The remaining overrides support the Stream API (sync/async writes, flushing, and disposal).
// Per-response state: like the underlying response stream, writes must not overlap.
internal sealed class LimitedResponseBuffer(Stream destination, HttpResponse response, long limit) : Stream
{
    private readonly MemoryStream _responseBytesForAnalysis = new();
    private readonly long _limit = Math.Clamp(limit, 0, Array.MaxLength);
    public bool ExceededLimit { get; private set; }
    public long BytesWritten { get; private set; }
    public byte[] ToArray() => _responseBytesForAnalysis.ToArray();

    private bool MustPassThrough(int count) => ExceededLimit ||
        response.ContentLength > _limit || count > _limit - _responseBytesForAnalysis.Length;

    private void Reserve(int count)
    {
        var required = _responseBytesForAnalysis.Length + count;
        if (required > _responseBytesForAnalysis.Capacity)
            _responseBytesForAnalysis.Capacity = (int)Math.Min(_limit, Math.Max(required, Math.Max(256L, _responseBytesForAnalysis.Capacity * 2L)));
    }

    public override void Write(byte[] incomingChunk, int offset, int count) => Write(incomingChunk.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> incomingChunk)
    {
        if (MustPassThrough(incomingChunk.Length))
        {
            if (!ExceededLimit)
            {
                _responseBytesForAnalysis.Position = 0;
                _responseBytesForAnalysis.CopyTo(destination);
                _responseBytesForAnalysis.SetLength(0);
                _responseBytesForAnalysis.Capacity = 0;
                ExceededLimit = true;
            }
            destination.Write(incomingChunk);
        }
        else
        {
            Reserve(incomingChunk.Length);
            _responseBytesForAnalysis.Write(incomingChunk);
        }
        BytesWritten += incomingChunk.Length;
    }

    public override Task WriteAsync(byte[] incomingChunk, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(incomingChunk.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> incomingChunk, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (MustPassThrough(incomingChunk.Length))
        {
            if (!ExceededLimit)
            {
                _responseBytesForAnalysis.Position = 0;
                await _responseBytesForAnalysis.CopyToAsync(destination, cancellationToken);
                _responseBytesForAnalysis.SetLength(0);
                _responseBytesForAnalysis.Capacity = 0;
                ExceededLimit = true;
            }
            await destination.WriteAsync(incomingChunk, cancellationToken);
        }
        else
        {
            Reserve(incomingChunk.Length);
            _responseBytesForAnalysis.Write(incomingChunk.Span);
        }
        BytesWritten += incomingChunk.Length;
    }

    public async Task CopyBufferedToAsync(CancellationToken cancellationToken)
    {
        if (ExceededLimit) return;
        _responseBytesForAnalysis.Position = 0;
        await _responseBytesForAnalysis.CopyToAsync(destination, cancellationToken);
    }

    public override void Flush() { if (ExceededLimit) destination.Flush(); }
    public override Task FlushAsync(CancellationToken cancellationToken) =>
        ExceededLimit ? destination.FlushAsync(cancellationToken) : Task.CompletedTask;
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
