using System.Text.Json;
using DeadlockClipper.Api.Data;
using DeadlockClipper.Core.Parsing;
using DeadlockClipper.Steam;
using Microsoft.EntityFrameworkCore;

namespace DeadlockClipper.Api.Jobs;

/// <summary>Saving match rosters (both teams and the winner) for "played with / against".</summary>
internal static class Rosters
{
    public static List<RosterPlayer> FromMetadata(CMsgMatchMetaDataContents contents) =>
        MetadataAnalysis.ToKills(contents).Players
            .Select(p => new RosterPlayer(p.SteamId, p.Team, p.HeroId, p.Kills, p.Deaths, p.Assists))
            .ToList();

    /// <summary>Adds or replaces the match's roster (tracked by the context; the caller saves).</summary>
    public static async Task SaveAsync(AppDbContext db, long matchId, CMsgMatchMetaDataContents? contents, JsonSerializerOptions json,
        CancellationToken ct)
    {
        var row = await db.Rosters.FindAsync([matchId], ct);
        if (row == null)
        {
            row = new MatchRoster { MatchId = matchId };
            db.Rosters.Add(row);
        }
        row.WinningTeam = contents == null ? Team.Unknown : MetadataAnalysis.WinningTeam(contents);
        row.PlayersJson = contents == null ? null : JsonSerializer.Serialize(FromMetadata(contents), json);
        row.FetchedAt = DateTimeOffset.UtcNow;
    }
}
