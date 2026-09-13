using Microsoft.AspNetCore.Http;

namespace CacheAnalyzer.AspNetCore;

// Per-response state: like the underlying response stream, writes must not overlap.
internal sealed class LimitedResponseBuffer(Stream destination, HttpResponse response, long limit) : Stream
{
    private readonly MemoryStream _buffer = new();
    private readonly long _limit = Math.Clamp(limit, 0, Array.MaxLength);
    public bool ExceededLimit { get; private set; }
    public long BytesWritten { get; private set; }
    public byte[] ToArray() => _buffer.ToArray();

    private bool MustPassThrough(int count) => ExceededLimit ||
        response.ContentLength > _limit || count > _limit - _buffer.Length;

    private void Reserve(int count)
    {
        var required = _buffer.Length + count;
        if (required > _buffer.Capacity)
            _buffer.Capacity = (int)Math.Min(_limit, Math.Max(required, Math.Max(256L, _buffer.Capacity * 2L)));
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (MustPassThrough(buffer.Length))
        {
            if (!ExceededLimit)
            {
                _buffer.Position = 0;
                _buffer.CopyTo(destination);
                _buffer.SetLength(0);
                _buffer.Capacity = 0;
                ExceededLimit = true;
            }
            destination.Write(buffer);
        }
        else
        {
            Reserve(buffer.Length);
            _buffer.Write(buffer);
        }
        BytesWritten += buffer.Length;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (MustPassThrough(buffer.Length))
        {
            if (!ExceededLimit)
            {
                _buffer.Position = 0;
                await _buffer.CopyToAsync(destination, cancellationToken);
                _buffer.SetLength(0);
                _buffer.Capacity = 0;
                ExceededLimit = true;
            }
            await destination.WriteAsync(buffer, cancellationToken);
        }
        else
        {
            Reserve(buffer.Length);
            _buffer.Write(buffer.Span);
        }
        BytesWritten += buffer.Length;
    }

    public async Task CopyBufferedToAsync(CancellationToken cancellationToken)
    {
        if (ExceededLimit) return;
        _buffer.Position = 0;
        await _buffer.CopyToAsync(destination, cancellationToken);
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
        if (disposing) _buffer.Dispose();
        base.Dispose(disposing); // The server owns destination.
    }
}
