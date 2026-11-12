using DeadlockClipper.Core.Highlights;
using DeadlockClipper.Core.Parsing;

namespace DeadlockClipper.Core.Tests;

public class TakedownDetectorTests
{
    private const ulong Drifter = 1, Pocket = 2, Dynamo = 3, Enemy1 = 7, Enemy2 = 8, Enemy3 = 9;
    private const int Rate = 64;

    private static KillEvent Kill(double seconds, ulong attacker, ulong victim, params ulong[] assisters) =>
        new((int)(seconds * Rate), seconds, attacker, "", 0, victim, "", 0, "", assisters);

    private static DetectionContext Context(params KillEvent[] kills) => new(Rate, 200_000,
        new ulong[] { Drifter, Pocket, Dynamo, Enemy1, Enemy2, Enemy3 }
            .Select(id => new PlayerInfo(id, $"p{id}", id < 7 ? Team.Sapphire : Team.Amber, (int)id, 0, 0, 0, 0, TopBarSlot: (int)id)).ToList(),
        kills);

    // Match 135279703, 34:39-34:44: a Dynamo ult; Pocket and Dynamo get the kills, Drifter assists all three.
    private static readonly KillEvent[] DynamoUlt =
    [
        Kill(2079.7, Pocket, Enemy1, Drifter, Dynamo),
        Kill(2081.8, Dynamo, Enemy2, Drifter, Pocket),
        Kill(2084.8, Dynamo, Enemy3, Drifter, Pocket),
    ];

    [Fact]
    public void AllAssists_IsATripleTakedownForTheAssister()
    {
        var highlights = new TakedownDetector().Detect(Context(DynamoUlt)).ToList();

        var mine = Assert.Single(highlights, h => h.PlayerSteamId == Drifter);
        Assert.Equal(HighlightType.TakedownStreak, mine.Type);
        Assert.Equal(3, mine.Kills.Count);
        Assert.Equal($"{Drifter}-td-{(int)(2079.7 * Rate)}", mine.Id);
        Assert.Equal("TRIPLE TAKEDOWN", TakedownDetector.Label(mine.Kills.Count));

        // Pocket (1 kill + 2 assists) and Dynamo (2 kills + 1 assist) made the same play.
        Assert.Equal([Drifter, Pocket, Dynamo], highlights.Select(h => h.PlayerSteamId).Order());
    }

    [Fact]
    public void PureKillStreaks_AreLeftToTheMultikillDetector()
    {
        var kills = new[] { Kill(100, Drifter, Enemy1), Kill(104, Drifter, Enemy2), Kill(108, Drifter, Enemy3) };
        Assert.DoesNotContain(new TakedownDetector().Detect(Context(kills)), h => h.PlayerSteamId == Drifter);
    }

    [Fact]
    public void GapsOver10s_BreakTheStreak_AndTwoTakedownsArentEnough()
    {
        var kills = new[] { Kill(100, Pocket, Enemy1, Drifter), Kill(109, Drifter, Enemy2), Kill(120, Pocket, Enemy3, Drifter) };
        Assert.DoesNotContain(new TakedownDetector().Detect(Context(kills)), h => h.PlayerSteamId == Drifter);
    }

    [Fact]
    public void ADoubleKillInsideAStreak_IsDropped_SoTheMomentIsListedOnce()
    {
        // Assist, then two own kills: a takedown streak containing a double kill.
        var kills = new[] { Kill(100, Pocket, Enemy1, Drifter), Kill(103, Drifter, Enemy2), Kill(106, Drifter, Enemy3) };
        var context = Context(kills);
        var all = new MultiKillDetector().Detect(context).Concat(new TakedownDetector().Detect(context)).ToList();
        Assert.Contains(all, h => h.Type == HighlightType.DoubleKill && h.PlayerSteamId == Drifter);

        var kept = TakedownDetector.RemoveCoveredMultiKills(all).ToList();

        Assert.DoesNotContain(kept, h => h.Type == HighlightType.DoubleKill && h.PlayerSteamId == Drifter);
        Assert.Contains(kept, h => h.Type == HighlightType.TakedownStreak && h.PlayerSteamId == Drifter);
    }

    [Fact]
    public void AllAssistTriple_RanksBetweenADoubleAndATripleKill()
    {
        var takedown = new TakedownDetector().Detect(Context(DynamoUlt)).Single(h => h.PlayerSteamId == Drifter);
        var triple = new MultiKillDetector().Detect(Context(
            Kill(2079.7, Drifter, Enemy1), Kill(2081.8, Drifter, Enemy2), Kill(2084.8, Drifter, Enemy3))).Single();
        var @double = new MultiKillDetector().Detect(Context(Kill(2079.7, Drifter, Enemy1), Kill(2081.8, Drifter, Enemy2))).Single();
        Assert.True(takedown.Score < triple.Score);
        Assert.True(takedown.Score > @double.Score, $"{takedown.Score} vs double {@double.Score}");
    }
}
