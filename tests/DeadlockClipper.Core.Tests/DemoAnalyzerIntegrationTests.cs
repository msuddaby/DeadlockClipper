using DeadlockClipper.Core.Parsing;
using Xunit.Abstractions;

namespace DeadlockClipper.Core.Tests;

/// <summary>
/// Parses a real demo from disk. Set DEADLOCK_TEST_DEMO to a .dem path; the test is a no-op when it isn't set
/// (demos are hundreds of MB and can't live in the repo).
/// </summary>
public class DemoAnalyzerIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ParsesRealDemo()
    {
        var path = Environment.GetEnvironmentVariable("DEADLOCK_TEST_DEMO");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            output.WriteLine("DEADLOCK_TEST_DEMO not set or missing; skipping.");
            return;
        }

        var analysis = await new DemoAnalyzer().AnalyzeFileAsync(path);

        output.WriteLine($"match={analysis.MatchId} map={analysis.Map} tickRate={analysis.TickRate} duration={analysis.DurationSeconds}s");
        foreach (var p in analysis.Players)
        {
            var feedKills = analysis.Kills.Count(k => k.AttackerSteamId == p.SteamId && k.VictimSteamId != p.SteamId);
            var feedDeaths = analysis.Kills.Count(k => k.VictimSteamId == p.SteamId);
            output.WriteLine($"  {p.Team} hero={p.HeroId} {p.Name} {p.Kills}/{p.Deaths}/{p.Assists} nw={p.NetWorth} | feed K={feedKills} D={feedDeaths}");
        }
        foreach (var h in analysis.Highlights)
            output.WriteLine($"  {h.Type} {h.PlayerName} @ {h.StartTimeSeconds:0}s ticks {h.ClipStartTick}-{h.ClipEndTick}");

        Assert.Equal(12, analysis.Players.Count);
        Assert.NotEmpty(analysis.Kills);
        Assert.All(analysis.Players, p => Assert.NotEqual(0, p.HeroId));
        if (analysis.MatchId is { } id && long.TryParse(Path.GetFileNameWithoutExtension(path), out var fileMatchId))
            Assert.Equal(fileMatchId, id); // replays are named <matchid>.dem

        // Every player gets a unique top-bar slot, Amber 1-6 and Sapphire 7-12.
        Assert.Equal(Enumerable.Range(1, 12), analysis.Players.Select(p => p.TopBarSlot).Order());
        Assert.All(analysis.Players, p => Assert.Equal(p.Team == Team.Amber, p.TopBarSlot <= 6));

        // Observed live in the game's top bar for this match.
        if (analysis.MatchId == 135555232)
        {
            Assert.Equal(8, analysis.Players.Single(p => p.Name == "Cthaeh").TopBarSlot);
            Assert.Equal(12, analysis.Players.Single(p => p.SteamId == 76561198125727691).TopBarSlot);
        }

        // The kill feed must agree with the scoreboard per player. If a Deadlock patch breaks entity decoding,
        // this is what catches it (values go out of sync long before anything throws).
        Assert.All(analysis.Players, p =>
        {
            Assert.Equal(p.Kills, analysis.Kills.Count(k => k.AttackerSteamId == p.SteamId && k.VictimSteamId != p.SteamId));
            Assert.Equal(p.Deaths, analysis.Kills.Count(k => k.VictimSteamId == p.SteamId));
        });
    }
}
