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

        var label = Label(highlight.Type);
        var marks = highlight.Kills
            .OrderBy(k => k.Tick)
            .Select((k, i) => new KillMark(
                OffsetSeconds: Math.Round((k.Tick - highlight.ClipStartTick) / analysis.TickRate, 2),
                Label: i == highlight.Kills.Count - 1 ? label : "KILL",
                IsFinal: i == highlight.Kills.Count - 1))
            .ToList();

        var hero = heroName?.Invoke(highlight.HeroId);
        var subtitle = string.Join("  ·  ", new[]
        {
            hero,
            ToTitleCase(label),
            FormatClock(highlight.StartTimeSeconds),
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
            OutputPath: outputPath);
    }

    public static string Label(HighlightType type) => type switch
    {
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
