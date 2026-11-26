using System.Text.Json;
using DeadlockClipper.Api.Data;
using DeadlockClipper.Core.Parsing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DeadlockClipper.Api.Tests;

public sealed class MatesTests : IDisposable
{
    private const ulong Me = 76561198000000001, Friend = 76561198000000002, Rival = 76561198000000003;
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly JsonSerializerOptions _json = Json.Create();

    public MatesTests() => _conn.Open();

    [Fact]
    public async Task CountsGamesAndResults_WithAndAgainst()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options);
        db.Database.EnsureCreated();
        // 1: friend with me, we win. 2: friend with me, we lose; rival against. 3: friend against me, I win. 4: no roster yet.
        Add(db, 1, Team.Amber, (Me, Team.Amber), (Friend, Team.Amber));
        Add(db, 2, Team.Sapphire, (Me, Team.Amber), (Friend, Team.Amber), (Rival, Team.Sapphire));
        Add(db, 3, Team.Sapphire, (Me, Team.Sapphire), (Friend, Team.Amber));
        db.Matches.Add(Match(4));
        db.PlayerNames.Add(new KnownPlayerName { SteamId = (long)Friend, Name = "buddy" });
        await db.SaveChangesAsync();

        var r = await PlayerStatsEndpoints.MatesAsync(Me, db, _json);

        Assert.Equal((4, 3, 1), (r.Matches, r.Counted, r.Waiting));
        var friend = r.Players[0];
        Assert.Equal(("buddy", new MateRecord(2, 1, 1), new MateRecord(1, 1, 0)), (friend.Name, friend.With, friend.Against));
        Assert.Equal(new MateRecord(1, 0, 1), r.Players.Single(p => p.SteamId == Rival.ToString()).Against);
    }

    private static ScannedMatch Match(long id) => new()
    {
        MatchId = id, PlayerSteamId = (long)Me, StartTime = DateTimeOffset.UtcNow.AddHours(-id), MetadataStatus = MetadataStatus.Done,
    };

    private void Add(AppDbContext db, long id, Team winner, params (ulong Id, Team Team)[] players)
    {
        db.Matches.Add(Match(id));
        db.Rosters.Add(new MatchRoster
        {
            MatchId = id, WinningTeam = winner, FetchedAt = DateTimeOffset.UtcNow,
            PlayersJson = JsonSerializer.Serialize(players.Select(p => new RosterPlayer(p.Id, p.Team, 1, 0, 0, 0)).ToList(), _json),
        });
    }

    public void Dispose() => _conn.Dispose();
}
