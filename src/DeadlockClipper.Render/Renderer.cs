using Microsoft.Extensions.Logging;

namespace DeadlockClipper.Render;

/// <summary>
/// Renders highlights from one demo: launch (if needed) → load demo → seek forward in hops → point the camera
/// → record in real time → finish with ffmpeg. Jobs run in tick order so one demo load serves many clips.
/// </summary>
public sealed class Renderer(
    RenderOptions options,
    IGameSession game,
    IRecorder recorder,
    IClipFinisher finisher,
    Func<string, string, CancellationToken, Task<string>> stageDemo,
    ILogger<Renderer> log,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public const int MaxAttempts = 2;

    public async Task<IReadOnlyList<RenderResult>> RenderBatchAsync(
        IReadOnlyList<RenderJob> jobs, IProgress<RenderProgress>? progress, CancellationToken ct)
    {
        var results = new List<RenderResult>();
        foreach (var job in jobs.OrderBy(j => j.StartTick))
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await RenderWithRetryAsync(job, progress, ct));
        }
        return results;
    }

    private async Task<RenderResult> RenderWithRetryAsync(RenderJob job, IProgress<RenderProgress>? progress, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await RenderOneAsync(job, progress, ct);
                progress?.Report(new RenderProgress(job.JobId, RenderStage.Done));
                return new RenderResult(job.JobId, true, job.OutputPath, null);
            }
            catch (GameCrashedException ex) when (attempt < MaxAttempts)
            {
                log.LogWarning("Game crashed rendering {Job} ({Message}); relaunching and retrying", job.JobId, ex.Message);
                game.LoadedDemo = null;
                await game.QuitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not GameInUseException)
            {
                log.LogError(ex, "Render failed for {Job}", job.JobId);
                progress?.Report(new RenderProgress(job.JobId, RenderStage.Failed, ex.Message));
                // Leave the game in a known state for the next job.
                if (ex is GameCrashedException) await game.QuitAsync(CancellationToken.None);
                return new RenderResult(job.JobId, false, null, ex.Message);
            }
        }
    }

    private async Task RenderOneAsync(RenderJob job, IProgress<RenderProgress>? progress, CancellationToken ct)
    {
        void Report(RenderStage stage, string detail = "") => progress?.Report(new RenderProgress(job.JobId, stage, detail));

        if (!game.IsLaunched || !await game.IsAliveAsync(ct))
        {
            Report(RenderStage.Launching);
            await game.LaunchAsync(ct);
        }

        var demoPath = await stageDemo(job.DemoKey, job.DemoPath, ct);
        var plan = SeekPlanner.Plan(game.CurrentTick, job.StartTick, options.SeekStepTicks);
        if (game.LoadedDemo != demoPath || plan.RequiresReload)
        {
            Report(RenderStage.LoadingDemo);
            await LoadDemoAsync(demoPath, ct);
            plan = SeekPlanner.Plan(0, job.StartTick, options.SeekStepTicks);
        }

        await game.SendAsync("demo_pause", ct);
        for (var i = 0; i < plan.Hops.Count; i++)
        {
            Report(RenderStage.Seeking, $"{i + 1}/{plan.Hops.Count}");
            await game.SendAsync($"demo_gototick {plan.Hops[i]}", ct);
            await _delay(TimeSpan.FromSeconds(options.HopSettleSeconds), ct);
            await EnsureAliveAsync($"seeking to tick {plan.Hops[i]}", ct);
            game.CurrentTick = plan.Hops[i];
        }

        await game.SendAsync(string.Join('\n',
            $"citadel_spectate_topbar_player {job.TopBarSlot}",
            "citadel_hud_visible 1",
            "citadel_hide_replay_hud 1"), ct);
        await _delay(TimeSpan.FromSeconds(2), ct);

        var workDir = Path.Combine(options.WorkDir, job.JobId);
        RawRecording raw;
        Report(RenderStage.Recording);
        await using (var recording = await recorder.StartAsync(workDir, ct))
        {
            await game.SendAsync("demo_resume", ct);
            // Playback is real time: the clip takes as long to record as it lasts.
            await _delay(TimeSpan.FromSeconds(job.DurationSeconds), ct);
            await game.SendAsync("demo_pause", ct);
            raw = await recording.StopAsync();
        }
        game.CurrentTick = job.EndTick;
        await EnsureAliveAsync("recording", ct);

        Report(RenderStage.Encoding);
        await finisher.FinishAsync(job, raw, workDir, ct);
        TryDelete(workDir);
    }

    private async Task LoadDemoAsync(string demoPath, CancellationToken ct)
    {
        game.MarkLog();
        await game.SendAsync($"playdemo {demoPath}", ct);
        if (!await game.WaitForLogAsync("Host activate: Playing Demo", TimeSpan.FromSeconds(60), ct))
            throw new TimeoutException($"Demo {demoPath} did not start playing.");
        // Seeking right after a load is unsafe; let the client settle first.
        await _delay(TimeSpan.FromSeconds(options.DemoSettleSeconds), ct);
        game.LoadedDemo = demoPath;
        game.CurrentTick = 0;
    }

    private async Task EnsureAliveAsync(string during, CancellationToken ct)
    {
        if (!await game.IsAliveAsync(ct)) throw new GameCrashedException($"Game exited while {during}.");
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
