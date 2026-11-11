using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using DeadlockClipper.Api.Data;
using DeadlockClipper.Core.Parsing;
using Microsoft.EntityFrameworkCore;

namespace DeadlockClipper.Api.Jobs;

/// <summary>In-process queue of demo ids to parse, plus live progress for the UI.</summary>
public sealed class ParseQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new() { SingleReader = true });
    private readonly ConcurrentDictionary<Guid, double> _progress = new();

    public void Enqueue(Guid id)
    {
        _progress[id] = 0;
        _channel.Writer.TryWrite(id);
    }

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);

    public double? ProgressOf(Guid id) => _progress.TryGetValue(id, out var p) ? p : null;
    internal void SetProgress(Guid id, double p) => _progress[id] = p;
    internal void Clear(Guid id) => _progress.TryRemove(id, out _);
}

public sealed class ParseWorker(
    ParseQueue queue,
    IServiceScopeFactory scopes,
    DemoAnalyzer analyzer,
    JsonSerializerOptions json,
    ILogger<ParseWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RequeueUnfinishedAsync(stoppingToken);

        // One demo at a time: parsing is CPU + IO heavy and demos are hundreds of MB.
        await foreach (var id in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ParseOneAsync(id, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            finally
            {
                queue.Clear(id);
            }
        }
    }

    private async Task ParseOneAsync(Guid id, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var demo = await db.Demos.FindAsync([id], ct);
        if (demo is null) return; // deleted while queued

        demo.Status = DemoStatus.Parsing;
        demo.Error = null;
        await db.SaveChangesAsync(ct);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (!File.Exists(demo.FilePath)) throw new FileNotFoundException("Demo file no longer exists.", demo.FilePath);

            var analysis = await analyzer.AnalyzeFileAsync(demo.FilePath, new Progress(queue, id), ct);

            demo.AnalysisJson = JsonSerializer.Serialize(analysis, json);
            demo.MatchId ??= analysis.MatchId;
            demo.Map = analysis.Map;
            demo.DurationSeconds = analysis.DurationSeconds;
            demo.HighlightCount = analysis.Highlights.Count;
            demo.AnalysisVersion = DemoAnalyzer.Version;
            demo.Status = DemoStatus.Done;
            demo.ParsedAt = DateTimeOffset.UtcNow;
            log.LogInformation("Parsed {File} in {Elapsed}: {Kills} kills, {Highlights} highlights",
                demo.FileName, sw.Elapsed, analysis.Kills.Count, analysis.Highlights.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Failed to parse {File}", demo.FileName);
            demo.Status = DemoStatus.Failed;
            demo.Error = ex.Message;
        }

        // Save even if the host is stopping, so the row doesn't stay stuck in Parsing.
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task RequeueUnfinishedAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Unfinished parses, plus demos analysed by an older analyzer (missing data newer features rely on).
        var pending = await db.Demos
            .Where(d => d.Status == DemoStatus.Queued || d.Status == DemoStatus.Parsing
                        || (d.Status == DemoStatus.Done && d.AnalysisVersion < DemoAnalyzer.Version))
            .OrderBy(d => d.CreatedAt)
            .ToListAsync(ct);
        if (pending.Count > 0) log.LogInformation("Queueing {Count} demos for (re)parsing", pending.Count);
        foreach (var d in pending)
        {
            d.Status = DemoStatus.Queued;
            queue.Enqueue(d.Id);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Synchronous progress sink (System.Progress posts to the thread pool, which reorders updates).</summary>
    private sealed class Progress(ParseQueue queue, Guid id) : IProgress<double>
    {
        public void Report(double value) => queue.SetProgress(id, value);
    }
}
