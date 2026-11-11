using System.Diagnostics;
using DeadlockClipper.Render;
using DeadlockClipper.Render.Overlays;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeadlockClipper.Render.Tests;

public class OverlayTests
{
    private const string GameDir = "/mnt/gamedrive/SteamLibrary/steamapps/common/Deadlock/game/citadel";

    // CSG (Drifter) double kill at 25:43: Soyah (Seven) at +5.0 s, FunniHeCool (Vindicta) at +13.14 s.
    private static RenderJob CsgDoubleKill(params KillMark[] kills) => new(
        "j1", "demo", "/x.dem", "h1", 98431, 99400, 64, 12,
        "COUNTER STRIKE GENIUS (150 IQ)", "Drifter  ·  Double Kill  ·  25:43  ·  Match 135555232",
        kills.Length > 0 ? kills :
        [
            new KillMark(5.0, "KILL", false, "Soyah ツ", 2),
            new KillMark(13.14, "DOUBLE KILL", true, "FunniHeCool", 3),
        ],
        "/out.mp4", "Counter Strike Genius (150 iq)", 64, "DOUBLE KILL");

    [Fact]
    public void Timeline_MatchesApprovedMockup()
    {
        var plan = OverlayTimeline.Plan(CsgDoubleKill());

        Assert.Equal(
        [
            new OverlayCue(OverlayKind.Intro, 0, 0.2, 3.0, OverlayAnimation.SlideFromLeft),
            new OverlayCue(OverlayKind.Kill, 0, 5.1, 6.2, OverlayAnimation.SlideFromLeft),
            new OverlayCue(OverlayKind.Kill, 1, 13.24, 14.14, OverlayAnimation.SlideFromLeft),
            new OverlayCue(OverlayKind.Final, 0, 13.39, 15.140625, OverlayAnimation.SlideFromRight),
        ], plan.Cues.Select(c => c with { Start = Math.Round(c.Start, 2), End = Math.Round(c.End, 6) }));
    }

    [Fact]
    public void Timeline_CloseKillsMakeWayForEachOther()
    {
        var plan = OverlayTimeline.Plan(CsgDoubleKill(
            new KillMark(5.0, "KILL", false), new KillMark(5.5, "KILL", false), new KillMark(9.0, "TRIPLE KILL", true)));

        var kills = plan.Cues.Where(c => c.Kind == OverlayKind.Kill).ToList();
        Assert.Equal(3, kills.Count);
        Assert.True(kills[0].End < kills[1].Start);
    }

    [Fact]
    public void Timeline_EarlyKillCutsIntroShort_SingleKillHasNoFinalBanner()
    {
        var plan = OverlayTimeline.Plan(CsgDoubleKill(new KillMark(2.0, "KILL", true)));

        Assert.Equal(2.05, plan.Cues.Single(c => c.Kind == OverlayKind.Intro).End, 2);
        Assert.DoesNotContain(plan.Cues, c => c.Kind == OverlayKind.Final);
    }

    /// <summary>
    /// End to end without the game: real hero art + Chromium + the ffmpeg overlay graph on a generated video.
    /// Runs only where Deadlock, chromium and ffmpeg are installed.
    /// </summary>
    [Fact]
    public async Task StyledOverlays_RenderAndCompositeWithRealArt()
    {
        if (!File.Exists(Path.Combine(GameDir, "pak01_dir.vpk")) || !OnPath("chromium") || !OnPath("ffmpeg")) return;

        var work = Path.Combine(Path.GetTempPath(), $"overlay-test-{Guid.NewGuid():N}");
        var options = new RenderOptions { GameDir = GameDir, WorkDir = work, AssetCacheDir = Path.Combine(work, "art") };
        using var artist = new OverlayArtist(options, new ProcessRunner(), NullLogger<OverlayArtist>.Instance);
        try
        {
            var job = CsgDoubleKill();
            var overlays = await artist.RenderAsync(job, work, default);

            Assert.NotNull(overlays);
            Assert.Equal(4, overlays.Layers.Count);
            Assert.All(overlays.Layers, l => Assert.True(new FileInfo(l.PngPath).Length > 5_000, l.PngPath));

            var graph = ClipComposer.BuildOverlayGraph(overlays, 60, 1920, 1080, 1);
            var output = Path.Combine(work, "out.mp4");
            var args = new List<string> { "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=60:duration=15.14" };
            foreach (var l in overlays.Layers)
                args.AddRange(["-loop", "1", "-framerate", "60", "-t",
                    ClipComposer.OverlayInputSeconds(l.Cue).ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", l.PngPath]);
            args.AddRange(["-filter_complex", graph, "-map", "[vout]", "-c:v", "libx264", "-preset", "ultrafast", output]);
            var r = await new ProcessRunner().RunAsync("ffmpeg", args, timeout: TimeSpan.FromMinutes(2));
            Assert.True(r.Success, r.StdErr);

            // Keep a frame of the final banner for visual inspection when debugging.
            var keep = Environment.GetEnvironmentVariable("OVERLAY_TEST_FRAMES");
            if (!string.IsNullOrEmpty(keep))
            {
                Directory.CreateDirectory(keep);
                foreach (var (t, name) in new[] { (1.5, "intro"), (5.6, "kill1"), (13.9, "final") })
                    await new ProcessRunner().RunAsync("ffmpeg", ["-v", "error", "-y", "-ss", t.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "-i", output, "-frames:v", "1", "-vf", "scale=960:-1", Path.Combine(keep, $"{name}.jpg")]);
            }
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, true);
        }
    }

    private static bool OnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(d => File.Exists(Path.Combine(d, exe)));
}
