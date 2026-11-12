using DeadlockClipper.Api.Jobs;
using DeadlockClipper.Core.Parsing;

namespace DeadlockClipper.Api.Tests;

public class AutoModePickTests
{
    private const ulong Me = 1, Mate = 2, Enemy = 7;

    private static KillEvent Kill(int tick, ulong attacker, params ulong[] assisters) =>
        new(tick, tick / 64.0, attacker, "", 0, Enemy, "", 0, "", assisters);

    private static Highlight H(string id, HighlightType type, ulong player, params KillEvent[] kills) =>
        new(id, type, player, "", 0, kills[0].Tick, kills[^1].Tick, 0, 0, kills[0].Tick, kills[^1].Tick, 1, kills);

    private static MatchAnalysis Analysis(IReadOnlyList<KillEvent> kills, params Highlight[] highlights) => new(
        1, "", 0, 64, 200_000, 3000,
        [new PlayerInfo(Me, "me", Team.Amber, 1, 0, 0, 0, 0, TopBarSlot: 1), new PlayerInfo(Mate, "mate", Team.Amber, 2, 0, 0, 0, 0, TopBarSlot: 2)],
        kills, highlights);

    [Fact]
    public void PicksMyMultikillsAndTakedowns_NotOthers()
    {
        var mine = H("a", HighlightType.DoubleKill, Me, Kill(100, Me), Kill(200, Me));
        var td = H("b", HighlightType.TakedownStreak, Me, Kill(300, Mate, Me), Kill(400, Me), Kill(500, Mate, Me));
        var theirs = H("c", HighlightType.TripleKill, Mate, Kill(600, Mate), Kill(700, Mate), Kill(800, Mate));

        var picks = AutoModeWorker.Pick(Analysis([], mine, td, theirs), new AutoModeSettings(), Me);

        Assert.Equal(["a", "b"], picks.Select(h => h.Id));
    }

    [Fact]
    public void Teamfights_NeedTwoOfMyKillsOrAssists()
    {
        var involved = H("f1", HighlightType.TeamFight, Mate, Kill(100, Mate, Me), Kill(200, Me), Kill(300, Mate));
        var barely = H("f2", HighlightType.TeamFight, Mate, Kill(1000, Mate, Me), Kill(1100, Mate), Kill(1200, Mate));

        var picks = AutoModeWorker.Pick(Analysis([], involved, barely), new AutoModeSettings(), Me);

        Assert.Equal(["f1"], picks.Select(h => h.Id));
    }

    [Fact]
    public void Compilation_OnlyWhenEnabledAndEnoughKills()
    {
        var kills = Enumerable.Range(0, 15).Select(i => Kill(10_000 + i * 2000, Me)).ToList();

        Assert.Empty(AutoModeWorker.Pick(Analysis(kills), new AutoModeSettings(), Me));
        var on = new AutoModeSettings { RenderCompilation = true, CompilationMinKills = 15 };
        Assert.Single(AutoModeWorker.Pick(Analysis(kills), on, Me), h => h.Type == HighlightType.KillCompilation);
        Assert.Empty(AutoModeWorker.Pick(Analysis(kills), on with { CompilationMinKills = 16 }, Me));
    }

    [Fact]
    public void NothingWhenImNotInTheMatch()
    {
        var mine = H("a", HighlightType.DoubleKill, Me, Kill(100, Me), Kill(200, Me));
        Assert.Empty(AutoModeWorker.Pick(Analysis([], mine), new AutoModeSettings(), 99));
    }
}
