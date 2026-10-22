using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SiemensMCPServer.Offline;

// Byte-preserving JSON-lines cap before the SDK accumulates a full request.
internal sealed class BoundedInputStream : Stream
{
    public const int MaxFrameBytes = 8 * 1024 * 1024;
    private readonly Stream inner;
    private int frameBytes;
    public BoundedInputStream(Stream inner) { this.inner = inner; }
    private int Check(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes) {
            if (value == 10) frameBytes = 0;
            else if (++frameBytes > MaxFrameBytes) throw new InvalidDataException("Offline input frame exceeds 8 MiB.");
        }
        return bytes.Length;
    }
    public override int Read(byte[] buffer, int offset, int count) { int read = inner.Read(buffer, offset, count); return Check(buffer.AsSpan(offset, read)); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); return Check(buffer.Span[..read]); }
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    { int read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false); return Check(buffer.AsSpan(offset, read)); }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override void Flush() => throw new NotSupportedException();
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
