using DeadlockClipper.Core.Highlights;
using DeadlockClipper.Core.Parsing;

namespace DeadlockClipper.Core.Tests;

public class KillCompilationTests
{
    private const ulong Me = 1, Other = 2;

    private static KillEvent Kill(int tick, ulong attacker = Me, ulong victim = Other) =>
        new(tick, tick / 64.0, attacker, "me", 7, victim, "them", 3, "", []);

    private static MatchAnalysis Match(params KillEvent[] kills) => new(
        1, "start", 0, 64, 200_000, 3000,
        [new PlayerInfo(Me, "Me", Team.Amber, 7, 0, 0, 0, 0, TopBarSlot: 1), new PlayerInfo(Other, "Other", Team.Sapphire, 3, 0, 0, 0, 0, TopBarSlot: 7)],
        kills, []);

    [Fact]
    public void Segments_PadEachKill_AndMergeOverlappingOrNearlyTouching()
    {
        var opts = new KillCompilationOptions(); // 4 s before, 2 s after, merge gaps <= 2 s, at 64 tick
        var segs = KillCompilation.Segments([10_000, 10_300, 20_000, 20_000 + 64 * 7, 50_000], 64, 200_000, opts);

        Assert.Equal(
        [
            new ClipSegment(10_000 - 256, 10_300 + 128),          // overlapping windows joined
            new ClipSegment(20_000 - 256, 20_000 + 64 * 7 + 128), // 1 s apart after padding: joined
            new ClipSegment(50_000 - 256, 50_000 + 128),
        ], segs);
    }

    [Fact]
    public void Segments_ClampToTheDemo()
    {
        var segs = KillCompilation.Segments([100, 9_990], 64, 10_000, new KillCompilationOptions { MergeGapSeconds = 0 });
        Assert.Equal([new ClipSegment(0, 228), new ClipSegment(9_990 - 256, 10_000)], segs);
    }

    [Fact]
    public void Build_UsesOnlyThePlayersHeroKills()
    {
        var analysis = Match(Kill(50_000), Kill(10_000), Kill(30_000, attacker: Other, victim: Me), Kill(40_000, victim: Me));

        var h = KillCompilation.Build(analysis, Me)!;

        Assert.Equal(HighlightType.KillCompilation, h.Type);
        Assert.Equal("kills-1", h.Id);
        Assert.Equal([10_000, 50_000], h.Kills.Select(k => k.Tick));
        Assert.Equal(2, h.Segments!.Count);
        Assert.Equal(10_000 - 256, h.ClipStartTick);
        Assert.Equal(50_000 + 128, h.ClipEndTick);
    }

    [Fact]
    public void Build_NeedsTwoKills()
    {
        Assert.Null(KillCompilation.Build(Match(Kill(10_000)), Me));
    }

    [Fact]
    public void Find_RebuildsCompilationsFromTheirId()
    {
        var analysis = Match(Kill(10_000), Kill(50_000));
        Assert.Equal(2, KillCompilation.Find(analysis, "kills-1")!.Kills.Count);
        Assert.Null(KillCompilation.Find(analysis, "kills-x"));
        Assert.Null(KillCompilation.Find(analysis, "1-10000"));
    }
}
