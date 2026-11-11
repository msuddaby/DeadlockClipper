using Microsoft.Extensions.Options;

namespace DeadlockClipper.Api.Storage;

public sealed class DemoStorage
{
    public string DataDir { get; }
    public string DemosDir { get; }

    public DemoStorage(IOptions<ClipperOptions> options, IHostEnvironment env)
    {
        DataDir = Path.GetFullPath(options.Value.DataDir, env.ContentRootPath);
        DemosDir = Path.Combine(DataDir, "demos");
        Directory.CreateDirectory(DemosDir);
    }

    public string PathFor(Guid id) => Path.Combine(DemosDir, $"{id}.dem");

    /// <summary>Streams to a temp file then renames, so a cancelled upload never leaves a half-written demo.</summary>
    public async Task<long> SaveAsync(Guid id, Stream content, long maxBytes, CancellationToken ct)
    {
        var final = PathFor(id);
        var temp = final + ".part";
        try
        {
            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                var buffer = new byte[1 << 20];
                long total = 0;
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > maxBytes) throw new InvalidOperationException($"Upload exceeds {maxBytes / (1024 * 1024)} MB limit.");
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            File.Move(temp, final, overwrite: true);
            return new FileInfo(final).Length;
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    /// <summary>Only deletes files we own (inside the demos dir) — never the game's replays.</summary>
    public void DeleteIfOwned(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(DemosDir + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(full))
            File.Delete(full);
    }
}
