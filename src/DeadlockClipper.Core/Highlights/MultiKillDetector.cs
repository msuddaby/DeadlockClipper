using DeadlockClipper.Core.Parsing;

namespace DeadlockClipper.Core.Highlights;

public sealed record MultiKillOptions
{
    /// <summary>Maximum gap between consecutive kills to keep the streak going.</summary>
    public double MaxGapSeconds { get; init; } = 10;

    /// <summary>Kills needed for a highlight (2 = double kill).</summary>
    public int MinKills { get; init; } = 2;

    public double PrePaddingSeconds { get; init; } = 5;
    public double PostPaddingSeconds { get; init; } = 2;
}

/// <summary>Chains each player's kills where the gap is within <see cref="MultiKillOptions.MaxGapSeconds"/>.</summary>
public sealed class MultiKillDetector(MultiKillOptions? options = null) : IHighlightDetector
{
    private readonly MultiKillOptions _options = options ?? new MultiKillOptions();

    public IEnumerable<Highlight> Detect(DetectionContext context)
    {
        var maxGapTicks = (int)Math.Round(_options.MaxGapSeconds * context.TickRate);
        var preTicks = (int)Math.Round(_options.PrePaddingSeconds * context.TickRate);
        var postTicks = (int)Math.Round(_options.PostPaddingSeconds * context.TickRate);
        var players = context.Players.ToDictionary(p => p.SteamId);

        var byAttacker = context.Kills
            .Where(k => k.AttackerSteamId is { } a && a != 0 && a != k.VictimSteamId)
            .GroupBy(k => k.AttackerSteamId!.Value);

        foreach (var group in byAttacker)
        {
            var streak = new List<KillEvent>();
            foreach (var kill in group.OrderBy(k => k.Tick))
            {
                if (streak.Count > 0 && kill.Tick - streak[^1].Tick > maxGapTicks)
                {
                    if (TryBuild(streak, out var h)) yield return h;
                    streak = [];
                }
                streak.Add(kill);
            }
            if (TryBuild(streak, out var last)) yield return last;
        }

        bool TryBuild(List<KillEvent> kills, out Highlight highlight)
        {
            highlight = null!;
            if (kills.Count < _options.MinKills) return false;

            var first = kills[0];
            var final = kills[^1];
            var steamId = first.AttackerSteamId!.Value;
            players.TryGetValue(steamId, out var player);

            highlight = new Highlight(
                Id: $"{steamId}-{first.Tick}",
                Type: TypeFor(kills.Count),
                PlayerSteamId: steamId,
                PlayerName: player?.Name ?? first.AttackerName ?? "Unknown",
                HeroId: player?.HeroId ?? first.AttackerHeroId ?? 0,
                StartTick: first.Tick,
                EndTick: final.Tick,
                StartTimeSeconds: first.GameTimeSeconds,
                EndTimeSeconds: final.GameTimeSeconds,
                ClipStartTick: Math.Max(0, first.Tick - preTicks),
                ClipEndTick: context.TotalTicks > 0 ? Math.Min(context.TotalTicks, final.Tick + postTicks) : final.Tick + postTicks,
                Score: Score(kills, context.TickRate),
                Kills: kills.ToArray());
            return true;
        }
    }

    public static HighlightType TypeFor(int kills) => kills switch
    {
        <= 2 => HighlightType.DoubleKill,
        3 => HighlightType.TripleKill,
        4 => HighlightType.QuadKill,
        5 => HighlightType.PentaKill,
        _ => HighlightType.MegaKill,
    };

    /// <summary>More kills dominate; a tighter streak scores slightly higher.</summary>
    private static double Score(List<KillEvent> kills, double tickRate)
    {
        var spanSeconds = (kills[^1].Tick - kills[0].Tick) / tickRate;
        var tightness = 1.0 / (1.0 + spanSeconds / 10.0);
        return Math.Round(Math.Pow(kills.Count, 2) * (1 + tightness), 2);
    }
}
