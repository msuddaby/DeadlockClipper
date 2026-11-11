using DeadlockClipper.Core.Highlights;
using DeadlockClipper.Core.Parsing;

namespace DeadlockClipper.Core.Tests;

public class MultiKillDetectorTests
{
    private const double TickRate = 64;
    private const ulong Alice = 1, Bob = 2, Victim1 = 11, Victim2 = 12, Victim3 = 13, Victim4 = 14, Victim5 = 15, Victim6 = 16;

    private static readonly PlayerInfo[] Players =
    [
        new(Alice, "Alice", Team.Amber, 13, 0, 0, 0, 0),
        new(Bob, "Bob", Team.Amber, 6, 0, 0, 0, 0),
    ];

    private static KillEvent Kill(ulong? attacker, ulong victim, double seconds) => new(
        Tick: (int)(seconds * TickRate),
        GameTimeSeconds: seconds,
        AttackerSteamId: attacker,
        AttackerName: attacker?.ToString(),
        AttackerHeroId: null,
        VictimSteamId: victim,
        VictimName: victim.ToString(),
        VictimHeroId: 1,
        Weapon: "",
        AssisterSteamIds: []);

    private static List<Highlight> Detect(MultiKillOptions? options, params KillEvent[] kills) =>
        new MultiKillDetector(options).Detect(new DetectionContext(TickRate, 64 * 3600, Players, kills)).ToList();

    [Fact]
    public void SingleKill_IsNotAHighlight()
    {
        Assert.Empty(Detect(null, Kill(Alice, Victim1, 100)));
    }

    [Fact]
    public void TwoKillsWithinGap_IsDoubleKill()
    {
        var h = Assert.Single(Detect(null, Kill(Alice, Victim1, 100), Kill(Alice, Victim2, 108)));
        Assert.Equal(HighlightType.DoubleKill, h.Type);
        Assert.Equal(Alice, h.PlayerSteamId);
        Assert.Equal("Alice", h.PlayerName);
        Assert.Equal(13, h.HeroId);
        Assert.Equal(2, h.Kills.Count);
        Assert.Equal($"{Alice}-{(int)(100 * TickRate)}", h.Id);
    }

    [Fact]
    public void GapLongerThanWindow_BreaksStreak()
    {
        Assert.Empty(Detect(null, Kill(Alice, Victim1, 100), Kill(Alice, Victim2, 111)));
    }

    [Fact]
    public void StreakChainsOnConsecutiveGaps_NotTotalSpan()
    {
        // 0 -> 9 -> 18 -> 27: total span 27s but every gap is 9s.
        var h = Assert.Single(Detect(null,
            Kill(Alice, Victim1, 0), Kill(Alice, Victim2, 9), Kill(Alice, Victim3, 18), Kill(Alice, Victim4, 27)));
        Assert.Equal(HighlightType.QuadKill, h.Type);
    }

    [Fact]
    public void TwoSeparateStreaks_ProduceTwoHighlights()
    {
        var hs = Detect(null,
            Kill(Alice, Victim1, 100), Kill(Alice, Victim2, 102),
            Kill(Alice, Victim3, 500), Kill(Alice, Victim4, 501), Kill(Alice, Victim5, 502));
        Assert.Equal(2, hs.Count);
        Assert.Contains(hs, h => h.Type == HighlightType.DoubleKill);
        Assert.Contains(hs, h => h.Type == HighlightType.TripleKill);
    }

    [Fact]
    public void KillsArePerAttacker_AndUnorderedInputIsHandled()
    {
        var hs = Detect(null,
            Kill(Bob, Victim3, 103), Kill(Alice, Victim2, 102), Kill(Alice, Victim1, 100), Kill(Bob, Victim4, 101));
        Assert.Equal(2, hs.Count);
        Assert.All(hs, h => Assert.Equal(HighlightType.DoubleKill, h.Type));
        Assert.Equal([Victim1, Victim2], hs.Single(h => h.PlayerSteamId == Alice).Kills.Select(k => k.VictimSteamId));
    }

    [Fact]
    public void NonPlayerAndSelfKills_AreIgnored()
    {
        Assert.Empty(Detect(null, Kill(null, Victim1, 100), Kill(null, Victim2, 101), Kill(Alice, Alice, 102), Kill(Alice, Victim3, 103)));
    }

    [Fact]
    public void ClipWindow_IsPaddedAndClamped()
    {
        var h = Assert.Single(Detect(null, Kill(Alice, Victim1, 2), Kill(Alice, Victim2, 4)));
        Assert.Equal(0, h.ClipStartTick); // 2s - 5s clamps to 0
        Assert.Equal((int)(6 * TickRate), h.ClipEndTick); // 4s + 2s
    }

    [Fact]
    public void MinKillsOption_IsRespected()
    {
        var options = new MultiKillOptions { MinKills = 3 };
        Assert.Empty(Detect(options, Kill(Alice, Victim1, 100), Kill(Alice, Victim2, 101)));
        Assert.Single(Detect(options, Kill(Alice, Victim1, 100), Kill(Alice, Victim2, 101), Kill(Alice, Victim3, 102)));
    }

    [Theory]
    [InlineData(2, HighlightType.DoubleKill)]
    [InlineData(3, HighlightType.TripleKill)]
    [InlineData(4, HighlightType.QuadKill)]
    [InlineData(5, HighlightType.PentaKill)]
    [InlineData(6, HighlightType.MegaKill)]
    public void TypeFor_MapsKillCount(int kills, HighlightType expected) =>
        Assert.Equal(expected, MultiKillDetector.TypeFor(kills));

    [Fact]
    public void MoreKills_ScoreHigher()
    {
        var hs = Detect(null,
            Kill(Alice, Victim1, 100), Kill(Alice, Victim2, 101),
            Kill(Bob, Victim3, 200), Kill(Bob, Victim4, 209), Kill(Bob, Victim5, 218), Kill(Bob, Victim6, 227));
        Assert.True(hs.Single(h => h.PlayerSteamId == Bob).Score > hs.Single(h => h.PlayerSteamId == Alice).Score);
    }
}
