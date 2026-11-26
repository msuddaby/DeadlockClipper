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
        // In a takedown streak some of the player's takedowns are assists.
        string Verb(KillEvent k) => k.AttackerSteamId == highlight.PlayerSteamId ? "KILL" : "ASSIST";
        var fight = highlight.Type == HighlightType.TeamFight;
        IReadOnlyList<ClipSegment> segments = highlight.Segments is { Count: > 0 } s
            ? s
            : [new ClipSegment(highlight.ClipStartTick, highlight.ClipEndTick)];
        // What victims typed after dying; the clip runs on (if needed) so it's in it.
        (var victimChat, segments) = VictimChat(analysis, highlight, segments);
        var marks = highlight.Kills
            .OrderBy(k => k.Tick)
            .Select((k, i) => new KillMark(
                OffsetSeconds: Math.Round(ClipOffset(k.Tick, segments, analysis.TickRate), 2),
                Label: i == highlight.Kills.Count - 1 ? label : Verb(k),
                IsFinal: i == highlight.Kills.Count - 1,
                VictimName: k.VictimName,
                VictimHeroId: k.VictimHeroId,
                ShowMarker: !fight,
                Verb: Verb(k)))
            .ToList();

        var hero = heroName?.Invoke(highlight.HeroId);
        var compilation = highlight.Type == HighlightType.KillCompilation;
        var subtitle = string.Join("  ·  ", (fight
            ? new[] { "Teamfight", $"{highlight.Kills.Count} kills", TeamScore(analysis, highlight), FormatClock(highlight.StartTimeSeconds) }
            : new[]
            {
                hero,
                compilation ? "Kill Compilation" : ToTitleCase(label),
                compilation ? $"{highlight.Kills.Count} kills" : FormatClock(highlight.StartTimeSeconds),
            })
            .Append(analysis.MatchId is { } m ? $"Match {m}" : null)
            .Where(s => !string.IsNullOrWhiteSpace(s)));

        // Segments with their own camera target follow that player, if the game can point at them.
        var slots = highlight.Segments is { Count: > 0 } && segments.Any(s => s.FocusSteamId != null)
            ? segments.Select(s => analysis.Players.FirstOrDefault(p => p.SteamId == s.FocusSteamId)?.TopBarSlot is >= 1 and <= 12 and var slot
                ? slot
                : player.TopBarSlot).ToList()
            : null;

        var byId = analysis.Players.ToDictionary(p => p.SteamId);
        var chats = victimChat.Select(c =>
        {
            byId.TryGetValue(c.SenderSteamId!.Value, out var sender);
            return new ChatCaption(Math.Round(ClipOffset(c.Tick, segments, analysis.TickRate), 2), sender?.Name ?? c.SenderName,
                sender?.HeroId ?? 0, sender?.Team ?? Team.Unknown, c.AllChat, c.Text);
        }).ToList();

        return new RenderJob(
            JobId: jobId,
            DemoKey: demoKey,
            DemoPath: demoPath,
            HighlightId: highlight.Id,
            StartTick: segments[0].StartTick,
            EndTick: segments[^1].EndTick,
            TickRate: analysis.TickRate,
            TopBarSlot: player.TopBarSlot,
            IntroTitle: player.Name.ToUpperInvariant(),
            IntroSubtitle: subtitle,
            KillMarks: marks,
            OutputPath: outputPath,
            PlayerName: player.Name,
            PlayerHeroId: highlight.HeroId,
            HighlightLabel: label,
            Segments: highlight.Segments is { Count: > 0 } ? segments : null,
            SegmentTopBarSlots: slots,
            Chats: chats);
    }

    /// <summary>How long after dying a victim's message still counts as a reaction.</summary>
    public const double ChatWindowSeconds = Core.Highlights.ChatKillOptions.DefaultReactionWindowSeconds;
    /// <summary>How long a chat bubble stays up (the clip runs on at least this long past the message).</summary>
    public const double ChatShowSeconds = 5;
    public const int MaxChats = 2;

    /// <summary>
    /// Messages sent by someone the player killed (or, in a takedown streak, helped kill) within
    /// <see cref="ChatWindowSeconds"/> of dying, at most <see cref="MaxChats"/>. The segment holding the kill is
    /// extended so each message gets its full time on screen, without running into the next segment.
    /// </summary>
    internal static (List<ChatMessage> Chat, IReadOnlyList<ClipSegment> Segments) VictimChat(
        MatchAnalysis analysis, Highlight highlight, IReadOnlyList<ClipSegment> segments)
    {
        if (analysis.Chat is not { Count: > 0 } chat) return ([], segments);
        var rate = analysis.TickRate;
        var takedowns = highlight.Type == HighlightType.TakedownStreak;
        var picks = highlight.Kills
            .Where(k => k.AttackerSteamId == highlight.PlayerSteamId || (takedowns && k.AssisterSteamIds.Contains(highlight.PlayerSteamId)))
            .SelectMany(k => chat
                .Where(m => m.SenderSteamId == k.VictimSteamId && m.Tick > k.Tick && m.Tick <= k.Tick + ChatWindowSeconds * rate)
                .Select(m => (Msg: m, Kill: k)))
            .DistinctBy(p => p.Msg)
            .OrderBy(p => p.Msg.Tick)
            .Take(MaxChats)
            .ToList();
        if (picks.Count == 0) return ([], segments);

        var segs = segments.ToList();
        foreach (var (msg, kill) in picks)
        {
            var i = segs.FindLastIndex(x => x.StartTick <= kill.Tick);
            if (i < 0) continue;
            var cap = i + 1 < segs.Count ? segs[i + 1].StartTick - 1 : analysis.TotalTicks > 0 ? analysis.TotalTicks : int.MaxValue;
            var needed = msg.Tick + (int)Math.Round((ChatShowSeconds + 0.5) * rate);
            segs[i] = segs[i] with { EndTick = Math.Min(Math.Max(segs[i].EndTick, needed), cap) };
        }
        // Keep only messages that ended up on screen for at least a second.
        var shown = picks.Where(p => segs.Any(x => p.Msg.Tick >= x.StartTick && p.Msg.Tick + rate <= x.EndTick)).Select(p => p.Msg).ToList();
        return (shown, segs);
    }

    /// <summary>"Hidden King 6–3 Archmother": kills per team in the fight, by the teams' in-game names.</summary>
    private static string? TeamScore(MatchAnalysis analysis, Highlight highlight)
    {
        var teams = analysis.Players.ToDictionary(p => p.SteamId, p => p.Team);
        int Count(Team t) => highlight.Kills.Count(k => k.AttackerSteamId is { } a && teams.GetValueOrDefault(a) == t);
        var amber = Count(Team.Amber);
        var sapphire = Count(Team.Sapphire);
        return amber + sapphire == 0 ? null : $"Hidden King {amber}–{sapphire} Archmother";
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
        HighlightType.TeamFight => "TEAMFIGHT",
        HighlightType.TakedownStreak => Core.Highlights.TakedownDetector.Label(kills),
        HighlightType.ChatKill => "KILL",
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
