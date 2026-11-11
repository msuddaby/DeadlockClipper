using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using DeadlockClipper.Api.Data;
using DeadlockClipper.Steam;
using Microsoft.EntityFrameworkCore;

namespace DeadlockClipper.Api.Jobs;

/// <summary>A multikill found in match metadata (times are whole seconds).</summary>
public sealed record MatchHighlightDto(string Type, double TimeSeconds, int Kills, double Score);

/// <summary>Salts saved on a scanned match, so a later download or scan doesn't spend another GC request on them.</summary>
internal static class StoredSalts
{
    public static MatchSalts? From(ScannedMatch? m) =>
        m is { ClusterId: { } cluster, ReplaySalt: { } replay, MetadataSalt: { } meta }
            ? new MatchSalts((ulong)m.MatchId, (uint)cluster, (uint)replay, (uint)meta, m.ReplayValidThrough)
            : null;

    public static void Save(ScannedMatch m, MatchSalts salts)
    {
        m.ClusterId = (int)salts.ClusterId;
        m.ReplaySalt = salts.ReplaySalt;
        m.MetadataSalt = salts.MetadataSalt;
        m.ReplayValidThrough = salts.ReplayValidThrough ?? m.ReplayValidThrough;
    }
}

/// <summary>Wakes the metadata scanner.</summary>
public sealed class MatchScanQueue
{
    private readonly SemaphoreSlim _signal = new(0);

    public void Notify()
    {
        if (_signal.CurrentCount == 0) _signal.Release();
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}

/// <summary>
/// Fetches metadata for pending matches one at a time (salts from the Game Coordinator, then the file from Valve's
/// replay servers) and scores each for the scanned player. The account pool paces requests per account to stay under
/// Valve's rate limit and rests accounts that hit it; this worker just waits when every account is resting.
/// </summary>
public sealed class MatchScanWorker(
    MatchScanQueue queue,
    IServiceScopeFactory scopes,
    GcPool gc,
    IHttpClientFactory http,
    JsonSerializerOptions json,
    ILogger<MatchScanWorker> log) : BackgroundService
{
    /// <summary>
    /// Valve keeps replays 21 days: replay_valid_through from the GC is exactly match start + 21 days. Older matches
    /// can't be downloaded, so they aren't worth one of the scarce metadata requests.
    /// </summary>
    internal static readonly TimeSpan ReplayLifetime = TimeSpan.FromDays(21);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // A download the user asked for gets the next GC request ahead of the background scan.
            if (await DownloadWaitingForSaltsAsync(stoppingToken))
            {
                await queue.WaitAsync(TimeSpan.FromSeconds(15), stoppingToken);
                continue;
            }

            var next = await NextPendingAsync(stoppingToken);
            if (next == null)
            {
                await queue.WaitAsync(Timeout.InfiniteTimeSpan, stoppingToken);
                continue;
            }

            var wait = await ScanAsync(next.Value, stoppingToken);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, stoppingToken);
        }
    }

    private async Task<bool> DownloadWaitingForSaltsAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var downloading = await db.Demos.Where(d => d.Status == DemoStatus.Downloading && d.MatchId != null)
            .Select(d => d.MatchId!.Value).ToListAsync(ct);
        if (downloading.Count == 0) return false;
        var withSalts = await db.Matches.Where(m => downloading.Contains(m.MatchId) && m.ReplaySalt != null).CountAsync(ct);
        return withSalts < downloading.Count;
    }

    private async Task<(long MatchId, long PlayerSteamId)?> NextPendingAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Matches with under two kills can't hold a multikill: score them without spending a GC request.
        await db.Matches.Where(x => x.MetadataStatus == MetadataStatus.Pending && x.Kills < 2)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.MetadataStatus, MetadataStatus.Done)
                .SetProperty(x => x.Score, 0.0)
                .SetProperty(x => x.HighlightsJson, "[]"), ct);

        // Expired replays can't be rendered: don't spend a request on them.
        var downloadableSince = DateTimeOffset.UtcNow - ReplayLifetime;
        var pending = await db.Matches.Where(x => x.MetadataStatus == MetadataStatus.Pending).ToListAsync(ct);
        var expired = pending.Where(x => x.StartTime < downloadableSince).ToList();
        foreach (var x in expired)
        {
            x.MetadataStatus = MetadataStatus.Unavailable;
            x.Error = "Replay expired (Valve keeps replays for 21 days).";
            x.ReplayValidThrough = x.StartTime + ReplayLifetime;
        }
        if (expired.Count > 0) await db.SaveChangesAsync(ct);

        // Most kills first (likeliest to have highlights), then newest.
        var m = pending
            .Where(x => x.StartTime >= downloadableSince)
            .OrderByDescending(x => x.Kills)
            .ThenByDescending(x => x.StartTime)
            .FirstOrDefault();
        return m == null ? null : (m.MatchId, m.PlayerSteamId);
    }

    /// <returns>How long to wait before the next request.</returns>
    private async Task<TimeSpan> ScanAsync((long MatchId, long PlayerSteamId) match, CancellationToken ct)
    {
        MetadataStatus status;
        string? error = null;
        MatchPotential? potential = null;
        DateTimeOffset? validThrough = null;
        MatchSalts? fetched = null;
        var wait = TimeSpan.Zero;

        try
        {
            MatchSalts? stored;
            await using (var lookup = scopes.CreateAsyncScope())
                stored = StoredSalts.From(await lookup.ServiceProvider.GetRequiredService<AppDbContext>().Matches.FindAsync([match.MatchId], ct));
            var salts = stored ?? await gc.GetMatchSaltsAsync((ulong)match.MatchId, MetadataAnalysis.ToAccountId((ulong)match.PlayerSteamId), ct);
            fetched = salts;
            validThrough = salts.ReplayValidThrough;
            var contents = await new ReplayDownloader(http.CreateClient("replays")).GetMetadataAsync(salts, ct);
            potential = MetadataAnalysis.Score(contents, (ulong)match.PlayerSteamId);
            status = MetadataStatus.Done;
        }
        catch (GcRequestException ex) when (ex.Result is GcResult.RateLimited or GcResult.TooBusy)
        {
            // Every account is resting; resume when the first one may try again. Stays Pending.
            var until = gc.AllLimitedUntil() ?? DateTimeOffset.UtcNow.AddMinutes(5);
            log.LogInformation("All Steam accounts rate-limited; pausing the scan until {Until:t}", until.ToLocalTime());
            return until - DateTimeOffset.UtcNow;
        }
        catch (GcRequestException ex) when (ex.Result is GcResult.InvalidMatch)
        {
            (status, error) = (MetadataStatus.Unavailable, ex.Message);
        }
        catch (GcRequestException ex) when (ex.Result is GcResult.MatchInFlight)
        {
            return TimeSpan.FromMinutes(1); // finishes soon; stays Pending
        }
        catch (FileNotFoundException ex)
        {
            (status, error) = (MetadataStatus.Unavailable, ex.Message);
        }
        catch (SteamLoginException ex)
        {
            // No usable account: leave everything pending; check again in a minute (or on the next scan request).
            log.LogWarning("Match scan paused: {Message}", ex.Message);
            await queue.WaitAsync(TimeSpan.FromMinutes(1), ct);
            return TimeSpan.Zero;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Metadata scan failed for match {Match}", match.MatchId);
            (status, error) = (MetadataStatus.Failed, ex.Message);
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Matches.FindAsync([match.MatchId], CancellationToken.None);
        if (row == null) return wait;
        row.MetadataStatus = status;
        row.Error = error;
        row.ScannedAt = DateTimeOffset.UtcNow;
        row.ReplayValidThrough = validThrough ?? row.ReplayValidThrough;
        if (fetched != null) StoredSalts.Save(row, fetched);
        if (potential != null)
        {
            row.Score = potential.Score;
            row.HighlightsJson = JsonSerializer.Serialize(
                potential.Highlights.Select(h => new MatchHighlightDto(h.Type.ToString(), h.StartTimeSeconds, h.Kills.Count, h.Score)).ToList(),
                json);
        }
        await db.SaveChangesAsync(CancellationToken.None);
        return wait;
    }
}

/// <summary>Demo ids waiting to be downloaded from Valve. Progress is reported through <see cref="ParseQueue"/>.</summary>
public sealed class DownloadQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new() { SingleReader = true });
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _active = new();

    public void Enqueue(Guid demoId) => _channel.Writer.TryWrite(demoId);
    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);

    /// <summary>Stops the download if it's running. A queued one is skipped once its demo is no longer Downloading.</summary>
    public void Cancel(Guid demoId)
    {
        if (_active.TryGetValue(demoId, out var cts)) cts.Cancel();
    }

    internal CancellationTokenSource Start(Guid demoId, CancellationToken stopping)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _active[demoId] = cts;
        return cts;
    }

    internal void Finish(Guid demoId)
    {
        if (_active.TryRemove(demoId, out var cts)) cts.Dispose();
    }
}

/// <summary>Downloads replays for demos added by match id, then hands them to the parse queue.</summary>
public sealed class DownloadWorker(
    DownloadQueue queue,
    ParseQueue parse,
    MatchScanQueue scan,
    IServiceScopeFactory scopes,
    GcPool gc,
    IHttpClientFactory http,
    ILogger<DownloadWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RequeueInterruptedAsync(stoppingToken);
        await foreach (var id in queue.ReadAllAsync(stoppingToken))
        {
            var cts = queue.Start(id, stoppingToken);
            try
            {
                await DownloadAsync(id, cts.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            finally
            {
                queue.Finish(id);
            }
        }
    }

    private async Task DownloadAsync(Guid id, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var demo = await db.Demos.FindAsync([id], ct);
        if (demo is not { Status: DemoStatus.Downloading, MatchId: { } matchId }) return;

        try
        {
            // A scanned match already has its salts; only unscanned ones cost a (rate-limited) GC request.
            var scanned = await db.Matches.FindAsync([matchId], ct);
            var salts = StoredSalts.From(scanned) ?? await SaltsWaitingOutRateLimitsAsync(demo, scanned, db, ct);
            if (scanned != null)
            {
                StoredSalts.Save(scanned, salts); // lets the scanner score this match without its own request
                scan.Notify();
            }
            demo.Error = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await new ReplayDownloader(http.CreateClient("replays"))
                .DownloadDemoAsync(salts, demo.FilePath, new SyncProgress(p => parse.SetProgress(id, p)), ct);
            demo.SizeBytes = new FileInfo(demo.FilePath).Length;
            demo.Status = DemoStatus.Queued;
            log.LogInformation("Downloaded match {Match} ({Mb} MB) in {Elapsed}", matchId, demo.SizeBytes / 1024 / 1024, sw.Elapsed);
        }
        // Cancelled by the user (not a shutdown: then it stays Downloading and resumes on the next start).
        catch (OperationCanceledException) when (!_stopping)
        {
            log.LogInformation("Download of match {Match} cancelled", matchId);
            demo.Status = DemoStatus.Failed;
            demo.Error = "Download cancelled.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Download failed for match {Match}", matchId);
            demo.Status = DemoStatus.Failed;
            demo.Error = $"Download failed: {ex.Message}";
        }
        await db.SaveChangesAsync(CancellationToken.None);
        if (demo.Status == DemoStatus.Queued) parse.Enqueue(demo.Id);
        else parse.Clear(demo.Id);
    }

    private volatile bool _stopping;

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        return base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Asks the GC for the match's salts, staying queued (with a note on the demo) while every account is
    /// rate-limited, until the replay would have expired anyway.
    /// </summary>
    private async Task<MatchSalts> SaltsWaitingOutRateLimitsAsync(Demo demo, ScannedMatch? match, AppDbContext db, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                return await gc.GetMatchSaltsAsync((ulong)demo.MatchId!.Value,
                    match == null ? 0 : MetadataAnalysis.ToAccountId((ulong)match.PlayerSteamId), ct);
            }
            catch (GcRequestException ex) when (ex.Result is GcResult.RateLimited or GcResult.TooBusy)
            {
                var until = gc.AllLimitedUntil() ?? DateTimeOffset.UtcNow.AddMinutes(5);
                if (match != null && until > match.StartTime + MatchScanWorker.ReplayLifetime)
                    throw new InvalidOperationException("Valve's rate limit outlasted this replay (they're kept 21 days).");
                demo.Error = $"Waiting for Valve's rate limit (next try {until.ToLocalTime():h:mm tt}).";
                await db.SaveChangesAsync(ct);
                log.LogInformation("Download of match {Match} waiting for the GC rate limit until {Until:t}", demo.MatchId, until.ToLocalTime());
                var wait = until - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            }
        }
    }

    private async Task RequeueInterruptedAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ids = await db.Demos.Where(d => d.Status == DemoStatus.Downloading).OrderBy(d => d.CreatedAt).Select(d => d.Id).ToListAsync(ct);
        foreach (var id in ids) queue.Enqueue(id);
    }

    private sealed class SyncProgress(Action<double> onReport) : IProgress<double>
    {
        public void Report(double value) => onReport(value);
    }
}
