using System.Diagnostics;
using DeadlockClipper.Core.Parsing;
using DeadlockClipper.Render;

namespace DeadlockClipper.Render.Tests;

public class CaptionTests
{
    private const ulong Csg = 76561198125727691;

    // CSG's 25:43 double kill in match 135555232, as parsed from the real demo.
    private static (MatchAnalysis, Highlight) CsgDoubleKill()
    {
        KillEvent Kill(int tick, string victim) =>
            new(tick, 0, Csg, "Counter Strike Genius (150 iq)", 64, 1, victim, 2, "", []);
        var kills = new[] { Kill(98751, "Soyah ツ"), Kill(99272, "FunniHeCool") };
        var highlight = new Highlight($"{Csg}-98751", HighlightType.DoubleKill, Csg, "Counter Strike Genius (150 iq)", 64,
            98751, 99272, 1543.4, 1551.5, 98431, 99400, 8, kills);
        var player = new PlayerInfo(Csg, "Counter Strike Genius (150 iq)", Team.Sapphire, 64, 23, 4, 18, 43112,
            Lane: 6, EntityIndex: 11, TopBarSlot: 12);
        var analysis = new MatchAnalysis(135555232, "start", 0, 64, 124000, 1940, [player], kills, [highlight]);
        return (analysis, highlight);
    }

    [Fact]
    public void Job_HasKillMarksTimedToKillTicks()
    {
        var (analysis, highlight) = CsgDoubleKill();
        var job = CaptionPlan.CreateJob("j1", "demo", "/x.dem", analysis, highlight, "/out.mp4", _ => "Holliday");

        Assert.Equal(12, job.TopBarSlot);
        Assert.Equal(98431, job.StartTick);
        Assert.Equal(99400, job.EndTick);
        Assert.Equal(15.14, job.DurationSeconds, 2);
        Assert.Equal(
            [new KillMark(5.0, "KILL", false), new KillMark(13.14, "DOUBLE KILL", true)],
            job.KillMarks);
        Assert.Equal("COUNTER STRIKE GENIUS (150 IQ)", job.IntroTitle);
        Assert.Equal("Holliday  ·  Double Kill  ·  25:43  ·  Match 135555232", job.IntroSubtitle);
    }

    [Fact]
    public void Job_RequiresTopBarSlot()
    {
        var (analysis, highlight) = CsgDoubleKill();
        var noSlot = analysis with { Players = [analysis.Players[0] with { TopBarSlot = 0 }] };
        Assert.Throws<InvalidOperationException>(() =>
            CaptionPlan.CreateJob("j1", "demo", "/x.dem", noSlot, highlight, "/out.mp4"));
    }

    [Fact]
    public void Composer_EmitsOneDrawtextPerCaption()
    {
        var (analysis, highlight) = CsgDoubleKill();
        var job = CaptionPlan.CreateJob("j1", "demo", "/x.dem", analysis, highlight, "/out.mp4");

        var plan = ClipComposer.Build(job, 60, 1080, "/tmp/texts");

        Assert.StartsWith("fps=60,", plan.FilterGraph);
        Assert.Equal(4, plan.FilterGraph.Split("drawtext=").Length - 1); // title, subtitle, KILL, DOUBLE KILL
        Assert.Equal(["COUNTER STRIKE GENIUS (150 IQ)", "KILL", "DOUBLE KILL"],
            plan.Texts.Where(t => !t.FileName.StartsWith("intro_sub")).Select(t => t.Text));
    }

    [Fact]
    public void Composer_ShortensKillCaptionWhenNextKillIsClose()
    {
        var (analysis, highlight) = CsgDoubleKill();
        var job = CaptionPlan.CreateJob("j1", "demo", "/x.dem", analysis, highlight, "/out.mp4")
            with { KillMarks = [new KillMark(5.0, "KILL", false), new KillMark(5.5, "DOUBLE KILL", true)] };

        var plan = ClipComposer.Build(job, 60, 1080, "/tmp/texts");

        // The first caption must have faded out (alpha 0) by 5.45s, before the final label starts.
        Assert.Contains(ClipComposer.FadeAlpha(5.0, 5.45), plan.FilterGraph);
    }

    /// <summary>Runs the real ffmpeg over a generated video to prove the filter graph and escaping are valid.</summary>
    [Fact]
    public async Task Composer_FilterGraphRunsInFfmpeg()
    {
        if (!CanRun("ffmpeg")) return;

        var dir = Path.Combine(Path.GetTempPath(), $"clipper test {Guid.NewGuid():N}"); // space on purpose
        Directory.CreateDirectory(dir);
        try
        {
            var (analysis, highlight) = CsgDoubleKill();
            var tricky = analysis with { Players = [analysis.Players[0] with { Name = "Soyah ツ: it's (100%)" }] };
            var job = CaptionPlan.CreateJob("j1", "demo", "/x.dem", tricky, highlight, "/out.mp4");
            var plan = ClipComposer.Build(job, 60, 1080, dir);
            foreach (var t in plan.Texts) await File.WriteAllTextAsync(Path.Combine(dir, t.FileName), t.Text);

            var psi = new ProcessStartInfo("ffmpeg")
            {
                RedirectStandardError = true,
                ArgumentList =
                {
                    "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=30:duration=16",
                    "-vf", plan.FilterGraph, "-f", "null", "-",
                },
            };
            using var p = Process.Start(psi)!;
            var err = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            Assert.True(p.ExitCode == 0, err);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static bool CanRun(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(d => File.Exists(Path.Combine(d, exe)));
}
