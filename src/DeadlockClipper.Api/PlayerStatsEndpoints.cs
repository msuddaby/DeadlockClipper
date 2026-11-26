using System.Text.Json;
using DeadlockClipper.Api.Data;
using DeadlockClipper.Api.Jobs;
using DeadlockClipper.Core.Parsing;
using DeadlockClipper.Steam;
using Microsoft.EntityFrameworkCore;

namespace DeadlockClipper.Api;

/// <param name="Wins">Wins for the player whose page this is (not the mate).</param>
public sealed record MateRecord(int Games, int Wins, int Losses);

/// <param name="HeroId">The hero they played most recently with/against you.</param>
public sealed record MateDto(string SteamId, string? Name, int HeroId, MateRecord With, MateRecord Against, DateTimeOffset? LastPlayed);

/// <param name="Matches">The player's known matches.</param>
/// <param name="Counted">Matches whose rosters are known and so counted.</param>
/// <param name="Waiting">Matches whose rosters are still being fetched in the background.</param>
public sealed record MatesDto(int Matches, int Counted, int Waiting, IReadOnlyList<MateDto> Players);

public sealed record NamesRequest(IReadOnlyList<string> SteamIds);

/// <summary>Who a player has played with and against, from match rosters (metadata) and parsed demos.</summary>
public static class PlayerStatsEndpoints
{
    private sealed record Game(DateTimeOffset? PlayedAt, Team Winner, IReadOnlyList<RosterPlayer> Players);

    public static void MapPlayerStatsApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/players/{steamId}/mates", async (string steamId, AppDbContext db, JsonSerializerOptions json) =>
        {
            if (!ulong.TryParse(steamId, out var me)) return Results.Problem("Invalid steamId.", statusCode: 400);
            return Results.Ok(await MatesAsync(me, db, json));
        });

        // Steam names for players (metadata only has account ids). Known ones come from the database; the rest are
        // asked of Steam through a clipper account and remembered.
        api.MapPost("/players/names", async (NamesRequest req, AppDbContext db, GcPool gc, CancellationToken ct) =>
        {
            var ids = req.SteamIds.Select(s => long.TryParse(s, out var id) ? id : 0).Where(id => id > 0).Distinct().Take(200).ToList();
            var known = await db.PlayerNames.Where(n => ids.Contains(n.SteamId)).ToDictionaryAsync(n => n.SteamId, ct);
            var ask = ids.Where(id => !known.ContainsKey(id)).ToList();
            if (ask.Count > 0)
            {
                try
                {
                    var found = await gc.GetPersonaNamesAsync(ask.Select(id => (ulong)id).ToList(), ct);
                    foreach (var (id, name) in found) await RememberNameAsync(db, (long)id, name, ct);
                    await db.SaveChangesAsync(ct);
                    foreach (var (id, name) in found) known[(long)id] = new KnownPlayerName { SteamId = (long)id, Name = name };
                }
                catch (Exception ex) when (ex is SteamLoginException or GcRequestException or TimeoutException)
                {
                    // No account online: answer with what's known.
                }
            }
            return Results.Ok(known.ToDictionary(k => k.Key.ToString(), k => k.Value.Name));
        });
    }

    internal static async Task RememberNameAsync(AppDbContext db, long steamId, string name, CancellationToken ct)
    {
        var row = await db.PlayerNames.FindAsync([steamId], ct);
        if (row == null) db.PlayerNames.Add(row = new KnownPlayerName { SteamId = steamId, Name = name });
        row.Name = name;
        row.UpdatedAt = DateTimeOffset.UtcNow;
    }

    internal static async Task<MatesDto> MatesAsync(ulong me, AppDbContext db, JsonSerializerOptions json)
    {
        var scanned = await db.Matches.Where(m => m.PlayerSteamId == (long)me).ToListAsync();
        var ids = scanned.Select(m => m.MatchId).ToList();
        var rosters = await db.Rosters.Where(r => ids.Contains(r.MatchId)).ToDictionaryAsync(r => r.MatchId);

        var games = new Dictionary<string, Game>();
        foreach (var m in scanned)
            if (rosters.TryGetValue(m.MatchId, out var r) && r.PlayersJson != null)
                games[m.MatchId.ToString()] = new Game(m.StartTime, r.WinningTeam,
                    JsonSerializer.Deserialize<List<RosterPlayer>>(r.PlayersJson, json) ?? []);

        // Parsed demos know the rosters (and names) too, including replays that aren't in the scanned history.
        var names = new Dictionary<ulong, string>();
        var demoOnly = 0;
        var started = scanned.ToDictionary(m => m.MatchId, m => m.StartTime);
        foreach (var d in await db.Demos.Where(d => d.Status == DemoStatus.Done && d.AnalysisJson != null).ToListAsync())
        {
            if (DemoOverview.For(d, json) is not { } o) continue;
            foreach (var p in o.Players.Where(p => p.Name.Length > 0)) names.TryAdd(p.SteamId, p.Name);
            if (o.Players.All(p => p.SteamId != me)) continue;
            var key = d.MatchId?.ToString() ?? d.Id.ToString();
            if (d.MatchId is not { } mid || !started.ContainsKey(mid)) demoOnly++;
            if (games.ContainsKey(key)) continue;
            games[key] = new Game(d.MatchId is { } id && started.TryGetValue(id, out var t) ? t : d.CreatedAt, o.WinningTeam,
                o.Players.Select(p => new RosterPlayer(p.SteamId, p.Team, p.HeroId, p.Kills, p.Deaths, p.Assists)).ToList());
        }

        var stats = new Dictionary<ulong, (int GW, int WW, int LW, int GA, int WA, int LA, int Hero, DateTimeOffset? Last)>();
        var counted = 0;
        foreach (var g in games.Values.OrderBy(g => g.PlayedAt))
        {
            var mine = g.Players.FirstOrDefault(p => p.SteamId == me);
            if (mine == null || mine.Team is not (Team.Amber or Team.Sapphire)) continue;
            counted++;
            bool? won = g.Winner is Team.Amber or Team.Sapphire ? g.Winner == mine.Team : null;
            foreach (var p in g.Players.Where(p => p.SteamId != me && p.SteamId != 0 && p.Team is Team.Amber or Team.Sapphire))
            {
                stats.TryGetValue(p.SteamId, out var s);
                if (p.Team == mine.Team) s = s with { GW = s.GW + 1, WW = s.WW + (won == true ? 1 : 0), LW = s.LW + (won == false ? 1 : 0) };
                else s = s with { GA = s.GA + 1, WA = s.WA + (won == true ? 1 : 0), LA = s.LA + (won == false ? 1 : 0) };
                stats[p.SteamId] = s with { Hero = p.HeroId, Last = g.PlayedAt };
            }
        }

        var top = stats.OrderByDescending(x => x.Value.GW + x.Value.GA).ThenByDescending(x => x.Value.Last).Take(200).ToList();
        var topIds = top.Select(x => (long)x.Key).ToList();
        foreach (var n in await db.PlayerNames.Where(n => topIds.Contains(n.SteamId)).ToListAsync()) names.TryAdd((ulong)n.SteamId, n.Name);

        var keptSince = DateTimeOffset.UtcNow - MatchScanWorker.ReplayLifetime;
        var waiting = scanned.Count(m => !rosters.ContainsKey(m.MatchId) && !games.ContainsKey(m.MatchId.ToString()) &&
                                         m.MetadataStatus != MetadataStatus.Unavailable &&
                                         (StoredSalts.From(m) != null || m.StartTime >= keptSince));

        return new MatesDto(scanned.Count + demoOnly, counted, waiting, top.Select(x => new MateDto(
            x.Key.ToString(), names.GetValueOrDefault(x.Key), x.Value.Hero,
            new MateRecord(x.Value.GW, x.Value.WW, x.Value.LW),
            new MateRecord(x.Value.GA, x.Value.WA, x.Value.LA),
            x.Value.Last)).ToList());
    }
}
