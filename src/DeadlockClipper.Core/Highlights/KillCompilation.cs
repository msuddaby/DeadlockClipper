using DeadlockClipper.Core.Parsing;

namespace DeadlockClipper.Core.Highlights;

public sealed record KillCompilationOptions
{
    public double PrePaddingSeconds { get; init; } = 4;
    public double PostPaddingSeconds { get; init; } = 2;

    /// <summary>Segments closer than this are joined rather than cut between (a cut that short is jarring).</summary>
    public double MergeGapSeconds { get; init; } = 2;

    public int MinKills { get; init; } = 2;
}

/// <summary>
/// All of one player's hero kills across a match, as one clip: a short window around each kill, with overlapping
/// windows (multikills, quick follow-ups) merged into one segment. Built on demand from the parsed kills, so it isn't
/// stored in the analysis; its id ("kills-{steamId}") is enough to rebuild it.
/// </summary>
public static class KillCompilation
{
    public const string IdPrefix = "kills-";

    public static string IdFor(ulong steamId) => IdPrefix + steamId;

    /// <summary>A highlight stored in the analysis, or a compilation rebuilt from its id.</summary>
    public static Highlight? Find(MatchAnalysis analysis, string highlightId) =>
        analysis.Highlights.FirstOrDefault(h => h.Id == highlightId)
        ?? (highlightId.StartsWith(IdPrefix, StringComparison.Ordinal) && ulong.TryParse(highlightId[IdPrefix.Length..], out var steamId)
            ? Build(analysis, steamId)
            : null);

    /// <summary>Null when the player has fewer than <see cref="KillCompilationOptions.MinKills"/> hero kills.</summary>
    public static Highlight? Build(MatchAnalysis analysis, ulong steamId, KillCompilationOptions? options = null)
    {
        options ??= new KillCompilationOptions();
        var kills = analysis.Kills
            .Where(k => k.AttackerSteamId == steamId && k.VictimSteamId != steamId)
            .OrderBy(k => k.Tick)
            .ToList();
        if (kills.Count < Math.Max(1, options.MinKills)) return null;

        var player = analysis.Players.FirstOrDefault(p => p.SteamId == steamId);
        var segments = Segments(kills.Select(k => k.Tick), analysis.TickRate, analysis.TotalTicks, options);
        var first = kills[0];
        var last = kills[^1];

        return new Highlight(
            Id: IdFor(steamId),
            Type: HighlightType.KillCompilation,
            PlayerSteamId: steamId,
            PlayerName: player?.Name ?? first.AttackerName ?? "Unknown",
            HeroId: player?.HeroId ?? first.AttackerHeroId ?? 0,
            StartTick: first.Tick,
            EndTick: last.Tick,
            StartTimeSeconds: first.GameTimeSeconds,
            EndTimeSeconds: last.GameTimeSeconds,
            ClipStartTick: segments[0].StartTick,
            ClipEndTick: segments[^1].EndTick,
            Score: kills.Count,
            Kills: kills,
            Segments: segments);
    }

    /// <summary>A padded window per kill tick, merged where windows overlap or nearly touch. Ticks must be sorted.</summary>
    public static IReadOnlyList<ClipSegment> Segments(IEnumerable<int> killTicks, double tickRate, int totalTicks, KillCompilationOptions options)
    {
        int Ticks(double seconds) => (int)Math.Round(seconds * tickRate);
        var pre = Ticks(options.PrePaddingSeconds);
        var post = Ticks(options.PostPaddingSeconds);
        var mergeGap = Ticks(options.MergeGapSeconds);

        var segments = new List<ClipSegment>();
        foreach (var tick in killTicks)
        {
            var start = Math.Max(0, tick - pre);
            var end = totalTicks > 0 ? Math.Min(totalTicks, tick + post) : tick + post;
            if (segments.Count > 0 && start - segments[^1].EndTick <= mergeGap)
                segments[^1] = segments[^1] with { EndTick = Math.Max(segments[^1].EndTick, end) };
            else
                segments.Add(new ClipSegment(start, end));
        }
        return segments;
    }
}
