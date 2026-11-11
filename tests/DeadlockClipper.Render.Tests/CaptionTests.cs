using System.Diagnostics;
using DeadlockClipper.Core.Highlights;
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
            [new KillMark(5.0, "KILL", false, "Soyah ツ", 2), new KillMark(13.14, "DOUBLE KILL", true, "FunniHeCool", 2)],
            job.KillMarks);
        Assert.Equal(64, job.PlayerHeroId);
        Assert.Equal("Counter Strike Genius (150 iq)", job.PlayerName);
        Assert.Equal("DOUBLE KILL", job.HighlightLabel);
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

    [Fact]
    public void CompilationJob_TimesKillsOnTheJoinedTimeline()
    {
        var (analysis, _) = CsgDoubleKill();
        KillEvent Kill(int tick) => new(tick, tick / 64.0, Csg, "Counter Strike Genius (150 iq)", 64, 1, "v", 2, "", []);
        var kills = new[] { Kill(10_000), Kill(10_200), Kill(50_000) };
        var compilation = KillCompilation.Build(analysis with { Kills = kills }, Csg)!;

        var job = CaptionPlan.CreateJob("j1", "demo", "/x.dem", analysis, compilation, "/out.mp4", _ => "Holliday");

        // Segments: 9744..10328 (9.125 s) then 49744..50128 (6 s).
        Assert.Equal([new ClipSegment(9744, 10328), new ClipSegment(49744, 50128)], job.Segments);
        Assert.Equal(15.125, job.DurationSeconds, 3);
        Assert.Equal([4.0, 7.12, 13.12], job.KillMarks.Select(k => k.OffsetSeconds));
        Assert.Equal(["KILL", "KILL", "3 KILLS"], job.KillMarks.Select(k => k.Label));
        Assert.Equal("3 KILLS", job.HighlightLabel);
        Assert.Equal("Holliday  ·  Kill Compilation  ·  3 kills  ·  Match 135555232", job.IntroSubtitle);
    }

    /// <summary>The real ffmpeg command on synthetic recordings: segments joined, plain captions, one without audio.</summary>
    [Fact]
    public async Task Finisher_JoinsSegmentsToThePlannedLength()
    {
        if (!CanRun("ffmpeg") || !CanRun("ffprobe")) return;

        var dir = Path.Combine(Path.GetTempPath(), $"clipper-join-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var runner = new ProcessRunner();
            var raws = new List<RawRecording>();
            for (var i = 0; i < 3; i++)
            {
                var v = Path.Combine(dir, $"v{i}.mp4");
                var a = Path.Combine(dir, $"a{i}.wav");
                Assert.True((await runner.RunAsync("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30:duration=4",
                    "-c:v", "libx264", "-preset", "ultrafast", v])).Success);
                if (i != 1)
                    Assert.True((await runner.RunAsync("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=4", "-ac", "2", a])).Success);
                raws.Add(new RawRecording(v, a));
            }

            var job = new RenderJob("j", "d", "/x.dem", "h", 0, 3000, 64, 1, "TITLE", "sub",
                [new KillMark(1, "KILL", false), new KillMark(7, "3 KILLS", true)], Path.Combine(dir, "out", "clip.mp4"),
                Segments: [new ClipSegment(0, 192), new ClipSegment(1000, 1160), new ClipSegment(2000, 2224)]);
            var options = new RenderOptions { Fps = 30, Width = 640, Height = 360, CaptionStyle = "plain" };

            await new ClipFinisher(options, runner).FinishAsync(job, raws, dir, default);

            var probe = await runner.RunAsync("ffprobe", ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", job.OutputPath]);
            Assert.Equal(job.DurationSeconds, double.Parse(probe.StdOut.Trim(), System.Globalization.CultureInfo.InvariantCulture), 1);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static bool CanRun(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(d => File.Exists(Path.Combine(d, exe)));
}
