namespace DeadlockClipper.Render;

/// <summary>
/// playdemo only accepts paths relative to the game's citadel directory. Demos already in the game's replays
/// folder are used in place; anything else (uploads) is copied into replays/clipper/ for the render session.
/// </summary>
public sealed class DemoStager(RenderOptions options)
{
    private string StageDir => Path.Combine(options.GameDir, "replays", "clipper");

    /// <returns>The path to pass to playdemo.</returns>
    public async Task<string> StageAsync(string demoKey, string demoPath, CancellationToken ct)
    {
        var gameDir = Path.GetFullPath(options.GameDir).TrimEnd('/') + "/";
        var full = Path.GetFullPath(demoPath);
        if (full.StartsWith(gameDir, StringComparison.Ordinal))
            return Path.ChangeExtension(full[gameDir.Length..], null);

        Directory.CreateDirectory(StageDir);
        var staged = Path.Combine(StageDir, $"{demoKey}.dem");
        var source = new FileInfo(full);
        if (!File.Exists(staged) || new FileInfo(staged).Length != source.Length)
        {
            var tmp = staged + ".part";
            await using (var src = source.OpenRead())
            await using (var dst = File.Create(tmp))
                await src.CopyToAsync(dst, ct);
            File.Move(tmp, staged, overwrite: true);
        }
        return $"replays/clipper/{demoKey}";
    }

    public void Cleanup()
    {
        if (Directory.Exists(StageDir)) Directory.Delete(StageDir, recursive: true);
    }
}
