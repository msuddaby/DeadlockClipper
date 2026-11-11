using DeadlockClipper.Render;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeadlockClipper.Render.Tests;

public class RendererTests
{
    private sealed class FakeGame : IGameSession
    {
        public List<string> Log { get; } = [];
        public int Launches { get; private set; }
        public bool Alive { get; set; }
        /// <summary>Crash the game the next time a command containing this text is sent.</summary>
        public string? CrashOn { get; set; }

        public bool IsLaunched { get; private set; }
        public string? LoadedDemo { get; set; }
        public int CurrentTick { get; set; }

        public Task LaunchAsync(CancellationToken ct)
        {
            Launches++;
            IsLaunched = Alive = true;
            Log.Add("<launch>");
            return Task.CompletedTask;
        }

        public Task SendAsync(string commands, CancellationToken ct)
        {
            Log.AddRange(commands.Split('\n'));
            if (CrashOn != null && commands.Contains(CrashOn))
            {
                CrashOn = null;
                Alive = false;
            }
            return Task.CompletedTask;
        }

        public void MarkLog() { }
        public Task<bool> WaitForLogAsync(string text, TimeSpan timeout, CancellationToken ct) => Task.FromResult(Alive);
        public Task<bool> IsAliveAsync(CancellationToken ct) => Task.FromResult(Alive);

        public Task QuitAsync(CancellationToken ct)
        {
            Log.Add("<quit>");
            IsLaunched = Alive = false;
            LoadedDemo = null;
            CurrentTick = 0;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRecorder(FakeGame game) : IRecorder
    {
        public Task<IActiveRecording> StartAsync(string workDir, CancellationToken ct)
        {
            game.Log.Add("<record>");
            return Task.FromResult<IActiveRecording>(new Recording(game));
        }

        private sealed class Recording(FakeGame game) : IActiveRecording
        {
            public Task<RawRecording> StopAsync()
            {
                game.Log.Add("<stop>");
                return Task.FromResult(new RawRecording("v", "a"));
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeFinisher : IClipFinisher
    {
        public List<string> Finished { get; } = [];

        public Task FinishAsync(RenderJob job, RawRecording raw, string workDir, CancellationToken ct)
        {
            Finished.Add(job.JobId);
            return Task.CompletedTask;
        }
    }

    private static RenderJob Job(string id, int start, int end, int slot = 12, string demo = "demo1") =>
        new(id, demo, $"/replays/{demo}.dem", $"h-{id}", start, end, 64, slot, "T", "S", [], $"/out/{id}.mp4");

    private static (Renderer, FakeGame, FakeFinisher) Create()
    {
        var options = new RenderOptions { SeekStepTicks = 20000, WorkDir = Path.GetTempPath() };
        var game = new FakeGame();
        var finisher = new FakeFinisher();
        var renderer = new Renderer(options, game, new FakeRecorder(game), finisher,
            (key, _, _) => Task.FromResult($"replays/{key}"), NullLogger<Renderer>.Instance,
            delay: (_, _) => Task.CompletedTask);
        return (renderer, game, finisher);
    }

    [Fact]
    public async Task SingleJob_RunsTheProvenSequence()
    {
        var (renderer, game, finisher) = Create();

        var results = await renderer.RenderBatchAsync([Job("a", 98431, 99400)], null, default);

        Assert.True(results.Single().Success);
        Assert.Equal(["a"], finisher.Finished);
        Assert.Equal(
        [
            "<launch>", "playdemo replays/demo1", "demo_pause",
            "demo_gototick 20000", "demo_gototick 40000", "demo_gototick 60000", "demo_gototick 80000", "demo_gototick 98431",
            "citadel_spectate_topbar_player 12", "citadel_hud_visible 1", "citadel_hide_replay_hud 1",
            "<record>", "demo_resume", "demo_pause", "<stop>",
        ], game.Log);
    }

    [Fact]
    public async Task JobsOnSameDemo_LoadOnceAndSeekForwardInTickOrder()
    {
        var (renderer, game, _) = Create();

        await renderer.RenderBatchAsync([Job("late", 98431, 99400, 12), Job("early", 83401, 84453, 8)], null, default);

        Assert.Equal(1, game.Log.Count(l => l.StartsWith("playdemo")));
        Assert.Equal(1, game.Launches);
        // "early" first (83401), then a single short hop forward to "late".
        var seeks = game.Log.Where(l => l.StartsWith("demo_gototick")).ToList();
        Assert.Equal("demo_gototick 83401", seeks[4]);
        Assert.Equal("demo_gototick 98431", seeks[5]);
        Assert.Equal(6, seeks.Count);
    }

    [Fact]
    public async Task OverlappingJob_ReloadsDemoInsteadOfSeekingBackwards()
    {
        var (renderer, game, _) = Create();

        await renderer.RenderBatchAsync([Job("a", 91302, 92173, 8), Job("b", 91789, 92852, 12)], null, default);

        // b starts before a ended, so the demo is reloaded rather than seeking backwards.
        Assert.Equal(2, game.Log.Count(l => l.StartsWith("playdemo")));
    }

    [Fact]
    public async Task CrashDuringSeek_RelaunchesAndRetriesOnce()
    {
        var (renderer, game, finisher) = Create();
        game.CrashOn = "demo_gototick 60000";

        var results = await renderer.RenderBatchAsync([Job("a", 98431, 99400)], null, default);

        Assert.True(results.Single().Success);
        Assert.Equal(2, game.Launches);
        Assert.Contains("<quit>", game.Log);
        Assert.Equal(["a"], finisher.Finished);
    }

    [Fact]
    public async Task RepeatedCrash_FailsJobButContinuesBatch()
    {
        var (renderer, game, finisher) = Create();
        var crashes = 0;
        game.CrashOn = "demo_gototick 98431";
        var progress = new SyncProgress(p =>
        {
            // Re-arm the crash for the retry of job "a" only.
            if (p.JobId == "a" && p.Stage == RenderStage.Launching && crashes++ == 1) game.CrashOn = "demo_gototick 98431";
        });

        var results = await renderer.RenderBatchAsync([Job("a", 98431, 99400), Job("b", 110000, 111000)], progress, default);

        Assert.False(results[0].Success);
        Assert.Contains("exited", results[0].Error);
        Assert.True(results[1].Success);
        Assert.Equal(["b"], finisher.Finished);
    }

    /// <summary>Progress&lt;T&gt; posts to the thread pool; the test needs synchronous callbacks.</summary>
    private sealed class SyncProgress(Action<RenderProgress> onReport) : IProgress<RenderProgress>
    {
        public void Report(RenderProgress value) => onReport(value);
    }
}
