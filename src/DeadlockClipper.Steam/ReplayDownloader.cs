using Google.Protobuf;
using ICSharpCode.SharpZipLib.BZip2;
using ZstdSharp;

namespace DeadlockClipper.Steam;

/// <summary>Fetches a match's files from Valve's replay servers once the Game Coordinator has given us its salts.</summary>
public sealed class ReplayDownloader(HttpClient http)
{
    /// <summary>The match's metadata (a few hundred KB): players, stats and every death with killer and time.</summary>
    public async Task<CMsgMatchMetaDataContents> GetMetadataAsync(MatchSalts salts, CancellationToken ct)
    {
        using var response = await http.GetAsync(salts.MetadataUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        EnsureFound(response, "metadata", salts.MatchId);
        await using var compressed = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        await using (var decompressed = await DecompressAsync(compressed, ct))
            await decompressed.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        return ParseMetadata(buffer);
    }

    /// <summary>The file is a CMsgMatchMetaData whose match_details holds the serialized contents.</summary>
    public static CMsgMatchMetaDataContents ParseMetadata(Stream stream)
    {
        var wrapper = CMsgMatchMetaData.Parser.ParseFrom(stream);
        return CMsgMatchMetaDataContents.Parser.ParseFrom(wrapper.MatchDetails);
    }

    /// <summary>Downloads and decompresses the demo (~100-300 MB) to <paramref name="destination"/>.</summary>
    public async Task DownloadDemoAsync(MatchSalts salts, string destination, IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await http.GetAsync(salts.DemoUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        EnsureFound(response, "replay", salts.MatchId);
        var total = response.Content.Headers.ContentLength;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var tmp = destination + ".part";
        try
        {
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var counting = new CountingStream(source, read => { if (total > 0) progress?.Report((double)read / total.Value); }))
            await using (var decompressed = await DecompressAsync(counting, ct))
            await using (var file = File.Create(tmp))
            {
                await decompressed.CopyToAsync(file, 1 << 20, ct);
            }
            File.Move(tmp, destination, overwrite: true);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    /// <summary>
    /// Despite the .bz2 names, Valve currently serves Zstandard files; older ones were bzip2. Picks the decoder from
    /// the magic bytes. The returned stream owns <paramref name="source"/>.
    /// </summary>
    internal static async Task<Stream> DecompressAsync(Stream source, CancellationToken ct)
    {
        var magic = new byte[4];
        var n = await source.ReadAtLeastAsync(magic, magic.Length, throwOnEndOfStream: false, ct);
        var stream = new PrefixedStream(magic.AsMemory(0, n), source);
        if (n == 4 && magic is [0x28, 0xB5, 0x2F, 0xFD]) return new DecompressionStream(stream);
        if (n >= 3 && magic is [(byte)'B', (byte)'Z', (byte)'h', ..]) return new BZip2InputStream(stream);
        await stream.DisposeAsync();
        throw new InvalidDataException($"Unrecognised replay file format (starts with {Convert.ToHexString(magic, 0, n)}).");
    }

    private static void EnsureFound(HttpResponseMessage response, string what, ulong matchId)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new FileNotFoundException($"Valve has no {what} for match {matchId} (it expired, or the match ended too early to record one).");
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Replays bytes already read from a non-seekable stream before continuing with the rest of it.</summary>
    private sealed class PrefixedStream(ReadOnlyMemory<byte> prefix, Stream inner) : Stream
    {
        private ReadOnlyMemory<byte> _prefix = prefix;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_prefix.IsEmpty) return inner.Read(buffer);
            var n = Math.Min(buffer.Length, _prefix.Length);
            _prefix.Span[..n].CopyTo(buffer);
            _prefix = _prefix[n..];
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            _prefix.IsEmpty ? inner.ReadAsync(buffer, ct) : ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Reports bytes read from the compressed download, for progress.</summary>
    private sealed class CountingStream(Stream inner, Action<long> onRead) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Track(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            Track(await inner.ReadAsync(buffer, ct));

        private int Track(int n)
        {
            _read += n;
            onRead(_read);
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
