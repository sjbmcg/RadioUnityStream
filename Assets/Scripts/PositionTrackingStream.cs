using System;
using System.IO;

/// <summary>
/// Wraps a non-seekable stream and tracks the read position manually.
/// Required because NLayer reads stream.Position even on non-seekable streams.
/// </summary>
public class PositionTrackingStream : Stream
{
    private readonly Stream _inner;
    private long _position;

    public PositionTrackingStream(Stream inner) => _inner = inner;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = _inner.Read(buffer, offset, count);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner?.Dispose();
        base.Dispose(disposing);
    }
}
