using DeadlockClipper.Core.Parsing;

namespace DeadlockClipper.Core.Highlights;

public sealed record DenseFightOptions
{
    /// <summary>Kills (either team) at most this far apart belong to the same fight.</summary>
    public double MaxGapSeconds { get; init; } = 30;

    /// <summary>
    /// Kills needed for a fight. Measured on real matches (70-130 hero kills each): 30 s / 8+ finds the 0-4 big
    /// fights a match has; looser rules swallow most of the match, tighter ones only find 5-6 kill skirmishes.
    /// </summary>
    public int MinKills { get; init; } = 8;

    /// <summary>A fight running longer than this is split, so one clip stays watchable.</summary>
    public double MaxSpanSeconds { get; init; } = 300;

    public KillCompilationOptions Segments { get; init; } = new() { PrePaddingSeconds = 4, PostPaddingSeconds = 2, MergeGapSeconds = 3 };
}

/// <summary>
/// Bursts of hero kills from both teams: kills chained while each follows the last within
/// <see cref="DenseFightOptions.MaxGapSeconds"/>. The clip is a padded window per kill, merged where they overlap, so
/// the quiet stretches between kills are cut. Each segment's camera follows whoever got the most kills in it.
/// </summary>
public sealed class DenseFightDetector(DenseFightOptions? options = null) : IHighlightDetector
{
    private readonly DenseFightOptions _options = options ?? new DenseFightOptions();

    public IEnumerable<Highlight> Detect(DetectionContext context)
    {
        var maxGap = _options.MaxGapSeconds * context.TickRate;
        var maxSpan = _options.MaxSpanSeconds * context.TickRate;
        var kills = context.Kills
            .Where(k => k.AttackerSteamId is { } a && a != 0 && a != k.VictimSteamId)
            .OrderBy(k => k.Tick)
            .ToList();

        var fight = new List<KillEvent>();
        foreach (var kill in kills)
        {
            if (fight.Count > 0 && (kill.Tick - fight[^1].Tick > maxGap || kill.Tick - fight[0].Tick > maxSpan))
            {
                if (Build(fight, context) is { } h) yield return h;
                fight = [];
            }
            fight.Add(kill);
        }
        if (Build(fight, context) is { } last) yield return last;
    }

    private Highlight? Build(List<KillEvent> kills, DetectionContext context)
    {
        if (kills.Count < _options.MinKills) return null;

        var segments = KillCompilation.Segments(kills.Select(k => k.Tick), context.TickRate, context.TotalTicks, _options.Segments)
            .Select(s => s with { FocusSteamId = TopKiller(kills.Where(k => k.Tick >= s.StartTick && k.Tick <= s.EndTick)) })
            .ToList();

        // The fight's top killer stands in as "the player" (intro card, final banner, default camera).
        var mvp = TopKiller(kills)!.Value;
        var player = context.Players.FirstOrDefault(p => p.SteamId == mvp);
        var first = kills[0];
        var final = kills[^1];
        var spanSeconds = (final.Tick - first.Tick) / context.TickRate;

        return new Highlight(
            Id: $"fight-{first.Tick}",
            Type: HighlightType.TeamFight,
            PlayerSteamId: mvp,
            PlayerName: player?.Name ?? kills.First(k => k.AttackerSteamId == mvp).AttackerName ?? "Unknown",
            HeroId: player?.HeroId ?? kills.First(k => k.AttackerSteamId == mvp).AttackerHeroId ?? 0,
            StartTick: first.Tick,
            EndTick: final.Tick,
            StartTimeSeconds: first.GameTimeSeconds,
            EndTimeSeconds: final.GameTimeSeconds,
            ClipStartTick: segments[0].StartTick,
            ClipEndTick: segments[^1].EndTick,
            // More kills dominate; packed tighter scores higher. On the same scale as multikills (a penta ~ 40-50).
            Score: Math.Round(kills.Count * 4 * (1 + 60 / (60 + spanSeconds)), 2),
            Kills: kills.ToArray(),
            Segments: segments);
    }

    /// <summary>Most kills; ties go to whoever got their first kill earliest.</summary>
    private static ulong? TopKiller(IEnumerable<KillEvent> kills) =>
        kills.GroupBy(k => k.AttackerSteamId!.Value)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Min(k => k.Tick))
            .Select(g => (ulong?)g.Key)
            .FirstOrDefault();
}
