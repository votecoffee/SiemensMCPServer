using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SiemensMCPServer.Mcp
{
    // Observe EOF at the actual pipe, before the SDK finishes queued requests.
    // Never read stdin from a second consumer or abort/replay a native call.
    internal sealed class NativeInputStream : Stream
    {
        private readonly Stream inner;
        private readonly Action ended;
        private int notified;
        public NativeInputStream(Stream inner, Action ended) { this.inner = inner; this.ended = ended; }
        private int Observed(int count, int requested) { if (requested > 0 && count == 0 && Interlocked.Exchange(ref notified, 1) == 0) ended(); return count; }
        public override int Read(byte[] buffer, int offset, int count) => Observed(inner.Read(buffer, offset, count), count);
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Observed(await inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false), count);
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override void Flush() => throw new NotSupportedException();
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
