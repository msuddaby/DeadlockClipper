using System.Text.Json;
using DeadlockClipper.Api.Data;
using DeadlockClipper.Api.Jobs;
using DeadlockClipper.Api.Storage;
using DeadlockClipper.Steam;
using Microsoft.EntityFrameworkCore;

namespace DeadlockClipper.Api;

/// <param name="Older">Continue past the oldest match already scanned instead of checking for new ones.</param>
public sealed record ScanRequest(string SteamId, bool Older = false);

public sealed record ScanResult(IReadOnlyList<MatchDto> Matches, bool ReachedEnd);

public sealed record AddAccountRequest(string Username, string Password);

public sealed record SteamCodeRequest(string Code);

/// <param name="ScanPausedUntil">Set while every account is rate-limited: metadata reading resumes then.</param>
public sealed record SteamStatusDto(IReadOnlyList<AccountStatus> Accounts, DateTimeOffset? ScanPausedUntil, double MetadataIntervalSeconds);

public sealed record MatchDto(
    long MatchId,
    DateTimeOffset StartTime,
    int DurationSeconds,
    int HeroId,
    int Kills,
    int Deaths,
    int Assists,
    int NetWorth,
    MetadataStatus MetadataStatus,
    string? Error,
    double? Score,
    IReadOnlyList<MatchHighlightDto> Highlights,
    DateTimeOffset? ReplayValidThrough,
    Guid? DemoId,
    DemoStatus? DemoStatus);

/// <summary>Finding matches worth clipping from match history + metadata, and downloading their replays.</summary>
public static class MatchEndpoints
{

    public static void MapMatchApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/steam/status", (GcPool gc, SteamOptions options) =>
            new SteamStatusDto(gc.Status(), gc.AllLimitedUntil(), options.MetadataIntervalSeconds));

        // TEMPORARY: what the request log says about Valve's rate limit so far.
        api.MapGet("/steam/quota", (QuotaLog log) => QuotaAnalysis.Analyze(log.Path, log.Read()));

        // Adding a clipper account: start with username/password, then poll; submit a Steam Guard code if asked.
        // Only the resulting refresh token is saved (see SteamAccountStore).
        api.MapPost("/steam/accounts", (AddAccountRequest req, SteamLogins logins) =>
        {
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrEmpty(req.Password))
                return Results.Problem("Username and password are required.", statusCode: 400);
            return Results.Ok(logins.Start(req.Username, req.Password));
        });
        api.MapGet("/steam/logins/{id:guid}", (Guid id, SteamLogins logins) =>
            logins.Get(id) is { } s ? Results.Ok(s) : Results.NotFound());
        api.MapPost("/steam/logins/{id:guid}/code", (Guid id, SteamCodeRequest req, SteamLogins logins) =>
            logins.SubmitCode(id, req.Code) ? Results.Ok(logins.Get(id)) : Results.Problem("Not waiting for a code.", statusCode: 409));
        api.MapDelete("/steam/accounts/{name}", async (string name, GcPool gc) =>
        {
            await gc.RemoveAsync(name);
            return Results.NoContent();
        });
        api.MapPost("/steam/accounts/{name}/verify", async (string name, GcPool gc, CancellationToken ct) =>
        {
            try
            {
                await gc.VerifyAsync(name, ct);
                return Results.NoContent();
            }
            catch (Exception ex) when (ex is SteamLoginException or GcRequestException or TimeoutException)
            {
                return Results.Problem(ex.Message, statusCode: 502);
            }
        });

        // Whose histories have been scanned (SteamID64 strings), most matches first: lets the page recover "me".
        api.MapGet("/matches/players", async (AppDbContext db) =>
            (await db.Matches.GroupBy(m => m.PlayerSteamId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync())
                .OrderByDescending(x => x.Count).Select(x => x.Key.ToString()));

        api.MapGet("/matches", async (string steamId, AppDbContext db, JsonSerializerOptions json) =>
        {
            if (!ulong.TryParse(steamId, out var id)) return Results.Problem("Invalid steamId.", statusCode: 400);
            return Results.Ok(await ListAsync((long)id, db, json));
        });

        api.MapPost("/matches/scan", async (ScanRequest req, AppDbContext db, GcPool gc, MatchScanQueue queue,
            JsonSerializerOptions json, CancellationToken ct) =>
        {
            if (!ulong.TryParse(req.SteamId, out var steamId)) return Results.Problem("Invalid steamId.", statusCode: 400);

            bool reachedEnd;
            try
            {
                reachedEnd = await MatchHistoryScanner.ScanAsync(db, gc, steamId, req.Older, ct);
            }
            catch (SteamLoginException ex)
            {
                return Results.Problem(ex.Message, statusCode: 503);
            }
            catch (GcRequestException ex)
            {
                return Results.Problem(ex.Message, statusCode: ex.Result == GcResult.InvalidPermission ? 403 : 502);
            }
            queue.Notify();
            return Results.Ok(new ScanResult(await ListAsync((long)steamId, db, json), reachedEnd));
        });

        api.MapPost("/matches/{matchId:long}/download", StartDownloadAsync);

        // Auto mode: settings, status, and a "run now" that also rescans history straight away.
        api.MapGet("/auto", (AutoModeState state) => new { state.Settings, Status = state.Status() });
        api.MapPut("/auto", (AutoModeSettings settings, AutoModeState state) =>
        {
            if (settings.Enabled && !ulong.TryParse(settings.SteamId, out _))
                return Results.Problem("Set your SteamID before turning auto mode on.", statusCode: 400);
            if (settings.MinScore < 0 || settings.MaxDownloadsPerDay < 0 || settings.ScanIntervalMinutes < 5 || settings.LookbackDays < 0)
                return Results.Problem("Invalid auto mode settings.", statusCode: 400);

            var previous = state.Settings;
            // Switching on starts the window; matches played earlier are left alone (except the look-back).
            var since = settings.Enabled && !previous.Enabled
                ? DateTimeOffset.UtcNow.AddDays(-settings.LookbackDays)
                : previous.Since;
            state.Save(settings with { Since = settings.Enabled ? since : null });
            state.Log(settings.Enabled == previous.Enabled ? "Settings changed" : settings.Enabled ? "Auto mode on" : "Auto mode off");
            return Results.Ok(new { state.Settings, Status = state.Status() });
        });
        api.MapPost("/auto/run", (AutoModeState state) =>
        {
            state.LastScanAt = null;
            state.Wake();
            return Results.Ok(new { state.Settings, Status = state.Status() });
        });

        api.MapPost("/demos/{id:guid}/cancel-download", async (Guid id, AppDbContext db, DownloadQueue downloads, ParseQueue parse) =>
        {
            var demo = await db.Demos.FindAsync(id);
            if (demo == null) return Results.NotFound();
            if (demo.Status != DemoStatus.Downloading) return Results.Problem("This demo isn't downloading.", statusCode: 409);
            demo.Status = DemoStatus.Failed;
            demo.Error = "Download cancelled.";
            await db.SaveChangesAsync();
            downloads.Cancel(id); // the worker also skips it if it hadn't started yet
            parse.Clear(id);
            return Results.Ok(Endpoints.ToSummary(demo, parse));
        });
    }

    /// <summary>Adds a demo for the match and queues its download; returns the existing demo if there already is one.</summary>
    public static async Task<IResult> StartDownloadAsync(long matchId, AppDbContext db, DemoStorage storage, DownloadQueue downloads, ParseQueue parse)
    {
        var (demo, created, expired) = await QueueDownloadAsync(matchId, db, storage, downloads, autoMode: false);
        if (expired) return Results.Problem("This match's replay has expired (Valve keeps replays for 21 days).", statusCode: 410);
        return created
            ? Results.Accepted($"/api/demos/{demo!.Id}", Endpoints.ToSummary(demo, parse))
            : Results.Ok(Endpoints.ToSummary(demo!, parse));
    }

    /// <summary>
    /// Adds a demo for the match and queues its download, or returns the existing one (retrying it if its download
    /// failed). Expired replays are refused before any GC request is spent.
    /// </summary>
    internal static async Task<(Demo? Demo, bool Created, bool Expired)> QueueDownloadAsync(
        long matchId, AppDbContext db, DemoStorage storage, DownloadQueue downloads, bool autoMode)
    {
        var existing = await db.Demos.Where(d => d.MatchId == matchId).OrderByDescending(d => d.CreatedAt).FirstOrDefaultAsync();
        if (existing is { Status: DemoStatus.Done or DemoStatus.Queued or DemoStatus.Parsing or DemoStatus.Downloading })
            return (existing, false, false);

        // Match ids aren't timestamps, but a scanned match knows when it was played. Checked before any retry: an
        // expired replay is gone, and asking the GC for its salts would only waste a rate-limited request.
        var scanned = await db.Matches.FindAsync(matchId);
        if (scanned != null && scanned.StartTime + MatchScanWorker.ReplayLifetime < DateTimeOffset.UtcNow)
            return (existing, false, true);

        if (existing is { Source: DemoSource.MatchId, Status: DemoStatus.Failed } && !File.Exists(existing.FilePath))
        {
            existing.Status = DemoStatus.Downloading; // the download failed earlier: try again
            existing.Error = null;
            await db.SaveChangesAsync();
            downloads.Enqueue(existing.Id);
        }
        if (existing != null) return (existing, false, false);

        var id = Guid.NewGuid();
        var demo = new Demo
        {
            Id = id,
            FileName = $"{matchId}.dem",
            FilePath = storage.PathFor(id),
            Source = DemoSource.MatchId,
            MatchId = matchId,
            Status = DemoStatus.Downloading,
            CreatedAt = DateTimeOffset.UtcNow,
            AutoMode = autoMode,
        };
        db.Demos.Add(demo);
        await db.SaveChangesAsync();
        downloads.Enqueue(demo.Id);
        return (demo, true, false);
    }

    private static async Task<List<MatchDto>> ListAsync(long steamId, AppDbContext db, JsonSerializerOptions json)
    {
        var matches = await db.Matches.Where(m => m.PlayerSteamId == steamId).OrderByDescending(m => m.StartTime).ToListAsync();
        var ids = matches.Select(m => (long?)m.MatchId).ToList();
        var demos = (await db.Demos.Where(d => ids.Contains(d.MatchId)).ToListAsync())
            .GroupBy(d => d.MatchId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.CreatedAt).First());

        return matches.Select(m =>
        {
            demos.TryGetValue(m.MatchId, out var demo);
            var highlights = m.HighlightsJson == null ? [] : JsonSerializer.Deserialize<List<MatchHighlightDto>>(m.HighlightsJson, json) ?? [];
            return new MatchDto(m.MatchId, m.StartTime, m.DurationSeconds, m.HeroId, m.Kills, m.Deaths, m.Assists, m.NetWorth,
                m.MetadataStatus, m.Error, m.Score, highlights, m.ReplayValidThrough, demo?.Id, demo?.Status);
        }).ToList();
    }
}

/// <summary>Reads a player's match history from the GC into the Matches table (new rows wait for the metadata scan).</summary>
public static class MatchHistoryScanner
{
    /// <summary>
    /// Pages (50 matches each) a newest-first scan may fetch; it stops early at the first page that reaches known
    /// matches or Valve's replay window. "Older" scans fetch one page past the oldest known match.
    /// </summary>
    private const int MaxNewPages = 4;

    /// <returns>True when there are no older matches left to fetch.</returns>
    /// <exception cref="SteamLoginException">No usable clipper account.</exception>
    /// <exception cref="GcRequestException">The GC refused (e.g. not a Steam friend of any clipper account).</exception>
    public static async Task<bool> ScanAsync(AppDbContext db, GcPool gc, ulong steamId, bool older, CancellationToken ct)
    {
        var history = new List<CMsgClientToGCGetMatchHistoryResponse.Match>();
        var reachedEnd = false;
        // The history cursor is a match id: the page after it holds older matches.
        ulong cursor = 0;
        if (older)
        {
            var oldest = await db.Matches.Where(m => m.PlayerSteamId == (long)steamId)
                .OrderBy(m => m.MatchId).Select(m => (long?)m.MatchId).FirstOrDefaultAsync(ct);
            cursor = (ulong)(oldest ?? 0);
        }
        var knownIds = (await db.Matches.Where(m => m.PlayerSteamId == (long)steamId).Select(m => m.MatchId).ToListAsync(ct)).ToHashSet();

        for (var page = 0; page < (older ? 1 : MaxNewPages); page++)
        {
            var r = await gc.GetMatchHistoryAsync(MetadataAnalysis.ToAccountId(steamId), cursor, ct);
            history.AddRange(r.Matches);
            if (r.ContinueCursor == 0 || r.Matches.Count == 0)
            {
                reachedEnd = true;
                break;
            }
            // Caught up with what we already have, or gone past what Valve still keeps.
            if (!older && r.Matches.Any(m => knownIds.Contains((long)m.MatchId))) break;
            if (r.Matches.Min(m => DateTimeOffset.FromUnixTimeSeconds(m.StartTime)) < DateTimeOffset.UtcNow - MatchScanWorker.ReplayLifetime)
            {
                reachedEnd = true;
                break;
            }
            cursor = r.ContinueCursor;
        }

        history = history.DistinctBy(m => m.MatchId).ToList();
        var ids = history.Select(m => (long)m.MatchId).ToList();
        var known = await db.Matches.Where(m => ids.Contains(m.MatchId)).ToDictionaryAsync(m => m.MatchId, ct);
        foreach (var m in history)
        {
            if (!known.TryGetValue((long)m.MatchId, out var row))
            {
                row = new ScannedMatch { MatchId = (long)m.MatchId, MetadataStatus = MetadataStatus.Pending };
                db.Matches.Add(row);
            }
            else if (row.MetadataStatus == MetadataStatus.Failed)
            {
                row.MetadataStatus = MetadataStatus.Pending; // retry failures on each scan
            }
            row.PlayerSteamId = (long)steamId;
            row.StartTime = DateTimeOffset.FromUnixTimeSeconds(m.StartTime);
            row.DurationSeconds = (int)m.MatchDurationS;
            row.HeroId = (int)m.HeroId;
            row.Kills = (int)m.PlayerKills;
            row.Deaths = (int)m.PlayerDeaths;
            row.Assists = (int)m.PlayerAssists;
            row.NetWorth = (int)m.NetWorth;
        }
        await db.SaveChangesAsync(ct);
        return reachedEnd;
    }
}
