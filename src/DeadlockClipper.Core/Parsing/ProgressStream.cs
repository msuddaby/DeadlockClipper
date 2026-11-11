namespace DeadlockClipper.Core.Parsing;

/// <summary>Read-only pass-through stream that reports read progress (0..1), throttled to 1% steps.</summary>
internal sealed class ProgressStream(Stream inner, IProgress<double> progress) : Stream
{
    private readonly long _length = inner.CanSeek ? inner.Length : 0;
    private int _lastPercent = -1;

    public override int Read(byte[] buffer, int offset, int count) => Report(inner.Read(buffer, offset, count));
    public override int Read(Span<byte> buffer) => Report(inner.Read(buffer));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        Report(await inner.ReadAsync(buffer, ct));

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        Report(await inner.ReadAsync(buffer.AsMemory(offset, count), ct));

    private int Report(int read)
    {
        if (_length > 0)
        {
            // Hold the last 1% back for the post-parse work; the analyzer reports 1.0 when done.
            var percent = (int)Math.Min(99, inner.Position * 100 / _length);
            if (percent != _lastPercent)
            {
                _lastPercent = percent;
                progress.Report(percent / 100.0);
            }
        }
        return read;
    }

    public override bool CanRead => true;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // The caller owns the inner stream.
        base.Dispose(disposing);
    }
}
