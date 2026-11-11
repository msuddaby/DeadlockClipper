using DeadlockClipper.Core.Highlights;
using DeadlockClipper.Core.Parsing;

namespace DeadlockClipper.Core.Tests;

public class DenseFightDetectorTests
{
    private const int Rate = 64;
    private static int S(double seconds) => (int)(seconds * Rate);

    private static KillEvent Kill(double seconds, ulong attacker, ulong victim = 99) =>
        new(S(seconds), seconds, attacker, $"p{attacker}", (int)attacker, victim, "v", 1, "", []);

    private static DetectionContext Context(params KillEvent[] kills) => new(Rate, S(3000),
        Enumerable.Range(1, 12).Select(i => new PlayerInfo((ulong)i, $"p{i}", i <= 6 ? Team.Amber : Team.Sapphire, i, 0, 0, 0, 0, TopBarSlot: i)).ToList(),
        kills);

    [Fact]
    public void EightKillsChainedWithin30s_IsAFight_WithQuietStretchesCut()
    {
        // 8 kills at 1000..1180 s, every ~25 s; then a lone kill 2 minutes later.
        var kills = Enumerable.Range(0, 8).Select(i => Kill(1000 + i * 25, (ulong)(i % 2 == 0 ? 1 : 7))).Append(Kill(1300, 2)).ToArray();

        var h = Assert.Single(new DenseFightDetector().Detect(Context(kills)));

        Assert.Equal(HighlightType.TeamFight, h.Type);
        Assert.Equal(8, h.Kills.Count);
        Assert.Equal($"fight-{S(1000)}", h.Id);
        // 25 s apart with 4 s before / 2 s after: 19 s of nothing between kills, so every kill is its own segment.
        Assert.Equal(8, h.Segments!.Count);
        Assert.All(h.Segments, s => Assert.Equal(S(6), s.EndTick - s.StartTick));
    }

    [Fact]
    public void FewerThanEightKills_OrGapsOver30s_AreNotFights()
    {
        var seven = Enumerable.Range(0, 7).Select(i => Kill(1000 + i * 5, 1)).ToArray();
        Assert.Empty(new DenseFightDetector().Detect(Context(seven)));

        var spread = Enumerable.Range(0, 10).Select(i => Kill(1000 + i * 31, 1)).ToArray();
        Assert.Empty(new DenseFightDetector().Detect(Context(spread)));
    }

    [Fact]
    public void CameraFollowsTheTopKillerOfEachSegment_AndTheFightsTopKillerIsThePlayer()
    {
        // Burst 1 (1000-1010): player 7 gets 3, player 1 gets 1. Burst 2 (1040-1050): player 2 gets 3, 7 gets 1.
        var kills = new[]
        {
            Kill(1000, 7), Kill(1003, 1), Kill(1006, 7), Kill(1010, 7),
            Kill(1040, 2), Kill(1044, 2), Kill(1047, 7), Kill(1050, 2),
        };

        var h = Assert.Single(new DenseFightDetector().Detect(Context(kills)));

        Assert.Equal([7UL, 2UL], h.Segments!.Select(s => s.FocusSteamId!.Value));
        Assert.Equal(7UL, h.PlayerSteamId); // 4 kills to player 2's 3
    }

    [Fact]
    public void FightsLongerThanFiveMinutes_AreSplit()
    {
        // 30 kills 20 s apart = 580 s of fighting.
        var kills = Enumerable.Range(0, 30).Select(i => Kill(1000 + i * 20, 1)).ToArray();

        var fights = new DenseFightDetector().Detect(Context(kills)).ToList();

        Assert.Equal(2, fights.Count);
        Assert.All(fights, f => Assert.True((f.EndTick - f.StartTick) / (double)Rate <= 300));
    }
}
