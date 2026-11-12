using System.Collections.Concurrent;
using System.Text.Json;
using DeadlockClipper.Api.Data;
using DeadlockClipper.Api.Storage;
using DeadlockClipper.Core.Highlights;
using DeadlockClipper.Core.Parsing;
using DeadlockClipper.Steam;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeadlockClipper.Api.Jobs;

/// <summary>What auto mode does. Saved server-side (it runs without the browser open).</summary>
public sealed record AutoModeSettings
{
    public bool Enabled { get; init; }

    /// <summary>Whose matches to clip (SteamID64).</summary>
    public string? SteamId { get; init; }

    /// <summary>Only matches played after this are downloaded/imported; set when auto mode is switched on.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>How far back to reach when switched on.</summary>
    public int LookbackDays { get; init; } = 2;

    /// <summary>Download matches whose metadata score is at least this (a double kill ~6, a triple ~13).</summary>
    public double MinScore { get; init; } = 12;

    /// <summary>Downloads are 200-600 MB each.</summary>
    public int MaxDownloadsPerDay { get; init; } = 5;

    public bool ImportLocalReplays { get; init; } = true;

    public bool RenderMultikills { get; init; } = true; // multikills and takedown streaks
    public bool RenderTeamfights { get; init; } = true; // fights you killed or assisted in at least twice
    public bool RenderCompilation { get; init; }
    public int CompilationMinKills { get; init; } = 15;

    public int ScanIntervalMinutes { get; init; } = 30;
}

public sealed record AutoModeActivity(DateTimeOffset At, string Message);

public sealed record AutoModeStatus(
    DateTimeOffset? LastScanAt,
    DateTimeOffset? NextScanAt,
    string? LastError,
    IReadOnlyList<AutoModeActivity> Recent);

/// <summary>Settings file plus the worker's live status.</summary>
public sealed class AutoModeState(DemoStorage storage)
{
    private readonly string _path = Path.Combine(storage.DataDir, "auto-mode.json");
    private readonly object _sync = new();
    private readonly ConcurrentQueue<AutoModeActivity> _recent = new();
    private readonly SemaphoreSlim _wake = new(0);
    private AutoModeSettings? _settings;

    public DateTimeOffset? LastScanAt { get; internal set; }
    public string? LastError { get; internal set; }

    public AutoModeSettings Settings
    {
        get
        {
            lock (_sync)
            {
                if (_settings != null) return _settings;
                try { _settings = File.Exists(_path) ? JsonSerializer.Deserialize<AutoModeSettings>(File.ReadAllText(_path)) : null; }
                catch (JsonException) { _settings = null; }
                return _settings ??= new AutoModeSettings();
            }
        }
    }

    public void Save(AutoModeSettings settings)
    {
        lock (_sync)
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            _settings = settings;
        }
        Wake();
    }

    public AutoModeStatus Status()
    {
        var s = Settings;
        DateTimeOffset? next = !s.Enabled ? null : LastScanAt is { } last ? last.AddMinutes(s.ScanIntervalMinutes) : DateTimeOffset.UtcNow;
        return new AutoModeStatus(LastScanAt, next, LastError, _recent.Reverse().ToList());
    }

    internal void Log(string message)
    {
        _recent.Enqueue(new AutoModeActivity(DateTimeOffset.UtcNow, message));
        while (_recent.Count > 30) _recent.TryDequeue(out _);
    }

    /// <summary>Run a pass now (settings changed, "Run now", or a demo finished parsing).</summary>
    public void Wake()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    internal Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _wake.WaitAsync(timeout, ct);
}

/// <summary>
/// Clips your matches hands-off: scans your history every <see cref="AutoModeSettings.ScanIntervalMinutes"/>,
/// downloads matches whose metadata scores well, imports new replays the game downloaded, and once a demo is parsed
/// queues renders of your highlights. Each pass is cheap when there's nothing to do, so it runs every minute.
/// </summary>
public sealed class AutoModeWorker(
    AutoModeState state,
    IServiceScopeFactory scopes,
    GcPool gc,
    MatchScanQueue scanQueue,
    DownloadQueue downloads,
    ParseQueue parse,
    RenderQueue renders,
    DemoStorage storage,
    IOptions<ClipperOptions> clipper,
    JsonSerializerOptions json,
    ILogger<AutoModeWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = state.Settings;
            if (settings is { Enabled: true, SteamId: { } sid } && ulong.TryParse(sid, out var steamId))
            {
                try
                {
                    await PassAsync(settings, steamId, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning(ex, "Auto mode pass failed");
                    state.LastError = ex.Message;
                }
            }
            await state.WaitAsync(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private async Task PassAsync(AutoModeSettings settings, ulong steamId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var since = settings.Since ?? DateTimeOffset.UtcNow.AddDays(-settings.LookbackDays);

        if (state.LastScanAt is not { } last || DateTimeOffset.UtcNow - last >= TimeSpan.FromMinutes(settings.ScanIntervalMinutes))
            await ScanAsync(db, steamId, ct);
        if (settings.ImportLocalReplays) await ImportLocalAsync(db, since, ct);
        await DownloadAsync(db, settings, steamId, since, ct);
        await RenderAsync(db, settings, steamId, ct);
    }

    private async Task ScanAsync(AppDbContext db, ulong steamId, CancellationToken ct)
    {
        state.LastScanAt = DateTimeOffset.UtcNow;
        try
        {
            var before = await db.Matches.CountAsync(m => m.PlayerSteamId == (long)steamId, ct);
            await MatchHistoryScanner.ScanAsync(db, gc, steamId, older: false, ct);
            var added = await db.Matches.CountAsync(m => m.PlayerSteamId == (long)steamId, ct) - before;
            if (added > 0) state.Log($"Found {added} new {(added == 1 ? "match" : "matches")} in your history");
            scanQueue.Notify();
            state.LastError = null;
        }
        catch (Exception ex) when (ex is SteamLoginException or GcRequestException)
        {
            state.LastError = $"History scan: {ex.Message}";
        }
    }

    /// <summary>Replays the game downloaded (you opened them in-game) since auto mode was switched on.</summary>
    private async Task ImportLocalAsync(AppDbContext db, DateTimeOffset since, CancellationToken ct)
    {
        var dir = clipper.Value.ReplaysDir;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
        var files = new DirectoryInfo(dir).GetFiles("*.dem").Where(f => f.LastWriteTimeUtc >= since.UtcDateTime).ToList();
        if (files.Count == 0) return;
        var paths = files.Select(f => f.FullName).ToList();
        var known = (await db.Demos.Where(d => paths.Contains(d.FilePath)).Select(d => d.FilePath).ToListAsync(ct)).ToHashSet();
        foreach (var f in files.Where(f => !known.Contains(f.FullName)))
        {
            // Skip a replay the game is still writing.
            if (DateTime.UtcNow - f.LastWriteTimeUtc < TimeSpan.FromMinutes(1)) continue;
            await Endpoints.ImportLocalAsync(f.FullName, db, parse, autoMode: true);
            state.Log($"Imported local replay {f.Name}");
        }
    }

    /// <summary>The best-scoring new matches, up to the daily cap.</summary>
    private async Task DownloadAsync(AppDbContext db, AutoModeSettings settings, ulong steamId, DateTimeOffset since, CancellationToken ct)
    {
        var dayAgo = DateTimeOffset.UtcNow.AddDays(-1);
        var recent = (await db.Demos.Where(d => d.AutoMode && d.Source == DemoSource.MatchId).Select(d => d.CreatedAt).ToListAsync(ct))
            .Count(c => c >= dayAgo);
        var budget = settings.MaxDownloadsPerDay - recent;
        if (budget <= 0) return;

        var stillKept = DateTimeOffset.UtcNow - MatchScanWorker.ReplayLifetime;
        var haveDemo = (await db.Demos.Where(d => d.MatchId != null).Select(d => d.MatchId!.Value).ToListAsync(ct)).ToHashSet();
        var candidates = (await db.Matches
                .Where(m => m.PlayerSteamId == (long)steamId && m.MetadataStatus == MetadataStatus.Done && m.Score >= settings.MinScore)
                .ToListAsync(ct))
            .Where(m => m.StartTime >= since && m.StartTime >= stillKept && !haveDemo.Contains(m.MatchId))
            .OrderByDescending(m => m.Score)
            .Take(budget);

        foreach (var m in candidates)
        {
            var (_, created, expired) = await MatchEndpoints.QueueDownloadAsync(m.MatchId, db, storage, downloads, autoMode: true);
            if (created) state.Log($"Downloading match {m.MatchId} (score {m.Score:0})");
            else if (expired) state.Log($"Match {m.MatchId}'s replay expired before it could be downloaded");
        }
    }

    /// <summary>Queues renders for auto-mode demos that finished parsing, once per demo.</summary>
    private async Task RenderAsync(AppDbContext db, AutoModeSettings settings, ulong steamId, CancellationToken ct)
    {
        var ready = await db.Demos.Where(d => d.AutoMode && d.AutoRenderedAt == null && d.Status == DemoStatus.Done && d.AnalysisJson != null)
            .ToListAsync(ct);
        foreach (var demo in ready)
        {
            var analysis = JsonSerializer.Deserialize<MatchAnalysis>(demo.AnalysisJson!, json)!;
            var picks = Pick(analysis, settings, steamId);
            foreach (var h in picks) await RenderEndpoints.EnqueueAsync(demo.Id, h, analysis, db);
            demo.AutoRenderedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            state.Log(picks.Count == 0
                ? $"Match {demo.MatchId}: nothing of yours to clip"
                : $"Match {demo.MatchId}: queued {picks.Count} {(picks.Count == 1 ? "clip" : "clips")}");
        }
        if (ready.Count > 0) renders.Notify();
    }

    /// <summary>Which of the match's highlights to render for the player.</summary>
    internal static List<Highlight> Pick(MatchAnalysis analysis, AutoModeSettings settings, ulong steamId)
    {
        var picks = new List<Highlight>();
        if (analysis.Players.All(p => p.SteamId != steamId)) return picks;

        if (settings.RenderMultikills)
            picks.AddRange(analysis.Highlights.Where(h => h.PlayerSteamId == steamId &&
                (MultiKillDetector.IsMultiKill(h.Type) || h.Type == HighlightType.TakedownStreak)));

        if (settings.RenderTeamfights)
            picks.AddRange(analysis.Highlights.Where(h => h.Type == HighlightType.TeamFight &&
                h.Kills.Count(k => k.AttackerSteamId == steamId || k.AssisterSteamIds.Contains(steamId)) >= 2));

        if (settings.RenderCompilation &&
            analysis.Kills.Count(k => k.AttackerSteamId == steamId && k.VictimSteamId != steamId) >= settings.CompilationMinKills &&
            KillCompilation.Build(analysis, steamId) is { } compilation)
            picks.Add(compilation);

        return picks.OrderBy(h => h.StartTick).ToList();
    }
}
