using DeadlockClipper.Core.Parsing;

namespace DeadlockClipper.Render;

/// <summary>Builds render jobs from parsed highlights: kill-timed captions and intro text.</summary>
public static class CaptionPlan
{
    public static RenderJob CreateJob(
        string jobId,
        string demoKey,
        string demoPath,
        MatchAnalysis analysis,
        Highlight highlight,
        string outputPath,
        Func<int, string>? heroName = null)
    {
        var player = analysis.Players.FirstOrDefault(p => p.SteamId == highlight.PlayerSteamId)
                     ?? throw new InvalidOperationException($"Player {highlight.PlayerSteamId} not found in analysis.");
        if (player.TopBarSlot is < 1 or > 12)
            throw new InvalidOperationException($"No top-bar slot for {player.Name}; cannot point the camera at them.");

        var label = Label(highlight.Type, highlight.Kills.Count);
        var segments = highlight.Segments is { Count: > 0 } s
            ? s
            : [new ClipSegment(highlight.ClipStartTick, highlight.ClipEndTick)];
        var marks = highlight.Kills
            .OrderBy(k => k.Tick)
            .Select((k, i) => new KillMark(
                OffsetSeconds: Math.Round(ClipOffset(k.Tick, segments, analysis.TickRate), 2),
                Label: i == highlight.Kills.Count - 1 ? label : "KILL",
                IsFinal: i == highlight.Kills.Count - 1,
                VictimName: k.VictimName,
                VictimHeroId: k.VictimHeroId))
            .ToList();

        var hero = heroName?.Invoke(highlight.HeroId);
        var compilation = highlight.Type == HighlightType.KillCompilation;
        var subtitle = string.Join("  ·  ", new[]
        {
            hero,
            compilation ? "Kill Compilation" : ToTitleCase(label),
            compilation ? $"{highlight.Kills.Count} kills" : FormatClock(highlight.StartTimeSeconds),
            analysis.MatchId is { } m ? $"Match {m}" : null,
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

        return new RenderJob(
            JobId: jobId,
            DemoKey: demoKey,
            DemoPath: demoPath,
            HighlightId: highlight.Id,
            StartTick: highlight.ClipStartTick,
            EndTick: highlight.ClipEndTick,
            TickRate: analysis.TickRate,
            TopBarSlot: player.TopBarSlot,
            IntroTitle: player.Name.ToUpperInvariant(),
            IntroSubtitle: subtitle,
            KillMarks: marks,
            OutputPath: outputPath,
            PlayerName: player.Name,
            PlayerHeroId: highlight.HeroId,
            HighlightLabel: label,
            Segments: highlight.Segments is { Count: > 0 } ? segments : null);
    }

    /// <summary>Seconds from the start of the finished clip to <paramref name="tick"/>, with the segments back to back.</summary>
    public static double ClipOffset(int tick, IReadOnlyList<ClipSegment> segments, double tickRate)
    {
        var before = 0;
        foreach (var s in segments)
        {
            if (tick <= s.EndTick) return (before + Math.Max(0, tick - s.StartTick)) / tickRate;
            before += s.EndTick - s.StartTick;
        }
        return before / tickRate;
    }

    public static string Label(HighlightType type, int kills = 0) => type switch
    {
        HighlightType.KillCompilation => $"{kills} KILLS",
        HighlightType.DoubleKill => "DOUBLE KILL",
        HighlightType.TripleKill => "TRIPLE KILL",
        HighlightType.QuadKill => "QUAD KILL",
        HighlightType.PentaKill => "PENTA KILL",
        _ => "MEGA KILL",
    };

    public static string FormatClock(double seconds)
    {
        var s = (int)Math.Round(Math.Max(0, seconds));
        return $"{s / 60}:{s % 60:00}";
    }

    private static string ToTitleCase(string s) =>
        string.Join(' ', s.Split(' ').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
}
