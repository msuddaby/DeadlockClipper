using System.Collections.Concurrent;
using System.Text.Json;
using DeadlockClipper.Api.Data;
using DeadlockClipper.Api.Heroes;
using DeadlockClipper.Core.Highlights;
using DeadlockClipper.Core.Parsing;
using DeadlockClipper.Render;
using Microsoft.EntityFrameworkCore;

namespace DeadlockClipper.Api.Jobs;

/// <summary>Wakes the render worker and holds live progress (stage/detail) for the UI.</summary>
public sealed class RenderQueue
{
    private readonly SemaphoreSlim _signal = new(0);
    private readonly ConcurrentDictionary<Guid, RenderProgress> _live = new();

    public void Notify()
    {
        if (_signal.CurrentCount == 0) _signal.Release();
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);

    public RenderProgress? Live(Guid id) => _live.TryGetValue(id, out var p) ? p : null;
    internal void SetLive(Guid id, RenderProgress p) => _live[id] = p;
    internal void ClearLive(Guid id) => _live.TryRemove(id, out _);
}

public sealed class RenderWorker(
    RenderQueue queue,
    IServiceScopeFactory scopes,
    Renderer renderer,
    IGameSession game,
    DemoStager stager,
    RenderOptions options,
    HeroCatalog heroes,
    JsonSerializerOptions json,
    ILogger<RenderWorker> log) : BackgroundService
{
    private static readonly TimeSpan IdleQuit = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan GameInUseRetry = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ResetInterruptedAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var batch = await NextBatchAsync(stoppingToken);
            if (batch.Count == 0)
            {
                if (!await queue.WaitAsync(IdleQuit, stoppingToken) && game.IsLaunched)
                {
                    log.LogInformation("Render queue idle; closing the game");
                    await game.QuitAsync(stoppingToken);
                    stager.Cleanup();
                }
                continue;
            }

            try
            {
                await RenderBatchAsync(batch, stoppingToken);
            }
            catch (GameInUseException ex)
            {
                // The player is in Deadlock; keep the batch queued and retry once they've closed it.
                await RequeueAsync(batch, stoppingToken);
                foreach (var r in batch) queue.SetLive(r.Id, new RenderProgress(r.Id.ToString(), RenderStage.Queued, ex.Message));
                await queue.WaitAsync(GameInUseRetry, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Render batch failed");
                await FailAsync(batch, ex.Message, CancellationToken.None);
            }
        }

        if (game.IsLaunched) await game.QuitAsync(CancellationToken.None);
    }

    /// <summary>All queued renders for the demo of the oldest queued render.</summary>
    private async Task<List<ClipRender>> NextBatchAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var first = await db.Renders.Where(r => r.Status == RenderStatus.Queued).OrderBy(r => r.CreatedAt).FirstOrDefaultAsync(ct);
        if (first == null) return [];
        return await db.Renders.AsNoTracking()
            .Where(r => r.Status == RenderStatus.Queued && r.DemoId == first.DemoId)
            .ToListAsync(ct);
    }

    private async Task RenderBatchAsync(List<ClipRender> batch, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var demo = await db.Demos.FindAsync([batch[0].DemoId], ct);
        if (demo?.AnalysisJson is null)
        {
            await FailAsync(batch, "Demo is missing or not parsed.", ct);
            return;
        }
        var analysis = JsonSerializer.Deserialize<MatchAnalysis>(demo.AnalysisJson, json)!;

        var jobs = new List<RenderJob>();
        foreach (var r in batch)
        {
            var highlight = KillCompilation.Find(analysis, r.HighlightId);
            if (highlight == null)
            {
                await FailAsync([r], "Highlight not found in the demo's analysis.", ct);
                continue;
            }
            try
            {
                jobs.Add(CaptionPlan.CreateJob(r.Id.ToString(), demo.Id.ToString("N"), demo.FilePath, analysis, highlight,
                    OutputPathFor(r, analysis), id => heroes.All.TryGetValue(id, out var h) ? h.Name : $"Hero #{id}"));
            }
            catch (InvalidOperationException ex)
            {
                await FailAsync([r], ex.Message, ct);
            }
        }
        if (jobs.Count == 0) return;

        var ids = jobs.Select(j => Guid.Parse(j.JobId)).ToList();
        await db.Renders.Where(r => ids.Contains(r.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RenderStatus.Rendering).SetProperty(r => r.Error, (string?)null), ct);

        // Persist each clip as soon as it finishes so it can be watched while the rest of the batch renders.
        var outputs = jobs.ToDictionary(j => j.JobId, j => j.OutputPath);
        var progress = new SyncProgress(p =>
        {
            var id = Guid.Parse(p.JobId);
            queue.SetLive(id, p);
            if (p.Stage is RenderStage.Done or RenderStage.Failed)
                SaveResult(id, p.Stage == RenderStage.Done, p.Stage == RenderStage.Done ? outputs[p.JobId] : null,
                    p.Stage == RenderStage.Failed ? p.Detail : null);
        });
        await renderer.RenderBatchAsync(jobs, progress, ct);
    }

    // Called from the renderer's progress callback, which runs synchronously on the worker's own thread.
    private void SaveResult(Guid id, bool success, string? outputPath, string? error)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Renders.Where(r => r.Id == id).ExecuteUpdate(s => s
            .SetProperty(r => r.Status, success ? RenderStatus.Done : RenderStatus.Failed)
            .SetProperty(r => r.OutputPath, outputPath)
            .SetProperty(r => r.Error, error)
            .SetProperty(r => r.FinishedAt, DateTimeOffset.UtcNow));
        queue.ClearLive(id);
    }

    private string OutputPathFor(ClipRender r, MatchAnalysis analysis)
    {
        var player = string.Concat(r.PlayerName.Select(c => char.IsLetterOrDigit(c) ? c : '_')).Trim('_');
        var clock = CaptionPlan.FormatClock(r.GameTimeSeconds).Replace(':', '-');
        var match = analysis.MatchId?.ToString() ?? "unknown-match";
        return Path.Combine(options.OutputDir, match, $"{player}_{r.HighlightType}_{clock}_{r.Id.ToString("N")[..6]}.mp4");
    }

    private async Task FailAsync(IEnumerable<ClipRender> renders, string error, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var r in renders)
        {
            await db.Renders.Where(x => x.Id == r.Id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, RenderStatus.Failed)
                .SetProperty(x => x.Error, error)
                .SetProperty(x => x.FinishedAt, DateTimeOffset.UtcNow), ct);
            queue.ClearLive(r.Id);
        }
    }

    private async Task RequeueAsync(IEnumerable<ClipRender> renders, CancellationToken ct)
    {
        var ids = renders.Select(r => r.Id).ToList();
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Renders.Where(r => ids.Contains(r.Id) && r.Status == RenderStatus.Rendering)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RenderStatus.Queued), ct);
    }

    /// <summary>Renders interrupted by a shutdown go back to the queue.</summary>
    private async Task ResetInterruptedAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Renders.Where(r => r.Status == RenderStatus.Rendering)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RenderStatus.Queued), ct);
    }

    private sealed class SyncProgress(Action<RenderProgress> onReport) : IProgress<RenderProgress>
    {
        public void Report(RenderProgress value) => onReport(value);
    }
}
