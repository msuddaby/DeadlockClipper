using DeadlockClipper.GameData;
using Microsoft.Extensions.Logging;

namespace DeadlockClipper.Render.Overlays;

public sealed record OverlayLayer(OverlayCue Cue, string PngPath);

public sealed record OverlayRender(IReadOnlyList<OverlayLayer> Layers, IReadOnlyList<double> Flashes);

public interface IOverlayArtist
{
    /// <summary>Renders the styled overlay graphics for a job, or returns null when they can't be made (plain captions are used then).</summary>
    Task<OverlayRender?> RenderAsync(RenderJob job, string workDir, CancellationToken ct);
}

/// <summary>Fills the overlay templates with the job's data and game art, and renders each to a transparent PNG with headless Chromium.</summary>
public sealed class OverlayArtist(RenderOptions options, IProcessRunner runner, ILogger<OverlayArtist> log) : IOverlayArtist, IDisposable
{
    private readonly Lazy<HeroAssets?> _assets = new(() =>
        File.Exists(Path.Combine(options.GameDir, "pak01_dir.vpk"))
            ? new HeroAssets(options.GameDir, string.IsNullOrWhiteSpace(options.AssetCacheDir)
                ? Path.Combine(options.WorkDir, "hero-art") : options.AssetCacheDir)
            : null);

    public async Task<OverlayRender?> RenderAsync(RenderJob job, string workDir, CancellationToken ct)
    {
        if (!string.Equals(options.CaptionStyle, "styled", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var art = CollectArt(job);
            var timeline = OverlayTimeline.Plan(job);
            var dir = Path.Combine(workDir, "overlays");
            Directory.CreateDirectory(dir);
            var profile = Path.Combine(dir, "chromium-profile");

            var layers = new List<OverlayLayer>();
            foreach (var cue in timeline.Cues)
            {
                var name = cue.Kind == OverlayKind.Kill ? $"kill_{cue.Index}" : cue.Kind.ToString().ToLowerInvariant();
                var html = cue.Kind switch
                {
                    OverlayKind.Intro => OverlayTemplates.Intro(job, art),
                    OverlayKind.Kill => OverlayTemplates.Kill(job, art, cue.Index),
                    _ => OverlayTemplates.Final(job, art),
                };
                var htmlPath = Path.Combine(dir, name + ".html");
                var pngPath = Path.Combine(dir, name + ".png");
                await File.WriteAllTextAsync(htmlPath, html, ct);
                await ScreenshotAsync(htmlPath, pngPath, profile, ct);
                layers.Add(new OverlayLayer(cue, pngPath));
            }
            return new OverlayRender(layers, timeline.Flashes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Styled overlays unavailable for {Job}; using plain captions", job.JobId);
            return null;
        }
    }

    private OverlayArt CollectArt(RenderJob job)
    {
        var assets = _assets.Value;
        string HeroName(int id) => assets?.Hero(id)?.Name ?? "";
        string? Image(int id, HeroImage image) => id > 0 ? assets?.Get(id, image) : null;

        return new OverlayArt(
            PlayerCard: Image(job.PlayerHeroId, HeroImage.Card),
            PlayerGloat: Image(job.PlayerHeroId, HeroImage.CardGloat),
            PlayerLogoSvg: Image(job.PlayerHeroId, HeroImage.Logo),
            PlayerHeroName: HeroName(job.PlayerHeroId),
            FontsDir: assets?.FontsDir is { } f && Directory.Exists(f) ? f : null,
            Victims: job.KillMarks.OrderBy(k => k.OffsetSeconds)
                .Select(k => (Image(k.VictimHeroId, HeroImage.Card), HeroName(k.VictimHeroId)))
                .ToList());
    }

    private async Task ScreenshotAsync(string htmlPath, string pngPath, string profile, CancellationToken ct)
    {
        File.Delete(pngPath);
        // Overlays are authored at 1920x1080; other render sizes are scaled in ffmpeg.
        var r = await runner.RunAsync(options.ChromiumPath,
        [
            "--headless=new", "--no-first-run", "--no-default-browser-check", "--disable-gpu",
            $"--user-data-dir={profile}", "--hide-scrollbars", "--allow-file-access-from-files",
            "--default-background-color=00000000", "--window-size=1920,1080", "--virtual-time-budget=2000",
            $"--screenshot={pngPath}", new Uri(htmlPath).AbsoluteUri,
        ], timeout: TimeSpan.FromSeconds(60), ct: ct);
        if (!File.Exists(pngPath)) throw new InvalidOperationException($"Chromium did not render {Path.GetFileName(htmlPath)}: {r.StdErr.Trim()}");
    }

    public void Dispose()
    {
        if (_assets.IsValueCreated) _assets.Value?.Dispose();
    }
}
