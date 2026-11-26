using DeadlockClipper.Core.Parsing;

namespace DeadlockClipper.Core.Highlights;

public sealed record ChatKillOptions
{
    /// <summary>How long after dying a victim's message still counts as a reaction to the kill.</summary>
    public const double DefaultReactionWindowSeconds = 20;

    public double ReactionWindowSeconds { get; init; } = DefaultReactionWindowSeconds;

    public double PrePaddingSeconds { get; init; } = 5;

    /// <summary>The render extends the clip so the message gets its time on screen; see CaptionPlan.VictimChat.</summary>
    public double PostPaddingSeconds { get; init; } = 2;
}

/// <summary>
/// A single kill whose victim typed in chat shortly after dying: one kill isn't a highlight, a salty "wtf" or "gg ez"
/// right after it is. Kills already inside one of the killer's multikills or takedown streaks are dropped by
/// <see cref="RemoveCovered"/>, since those clips show the same message.
/// </summary>
public sealed class ChatKillDetector(ChatKillOptions? options = null) : IHighlightDetector
{
    private readonly ChatKillOptions _options = options ?? new ChatKillOptions();

    /// <summary>Below a double kill (~6-8): worth a clip, not worth more than a real multikill.</summary>
    public const double BaseScore = 3;

    public IEnumerable<Highlight> Detect(DetectionContext context)
    {
        if (context.Chat is not { Count: > 0 } chat) yield break;
        var window = _options.ReactionWindowSeconds * context.TickRate;
        var pre = (int)Math.Round(_options.PrePaddingSeconds * context.TickRate);
        var post = (int)Math.Round(_options.PostPaddingSeconds * context.TickRate);
        var players = context.Players.ToDictionary(p => p.SteamId);
        var bySender = chat.Where(m => m.SenderSteamId is not null).ToLookup(m => m.SenderSteamId!.Value);

        foreach (var kill in context.Kills.OrderBy(k => k.Tick))
        {
            if (kill.AttackerSteamId is not { } attacker || attacker == 0 || attacker == kill.VictimSteamId) continue;
            var replies = bySender[kill.VictimSteamId].Count(m => m.Tick > kill.Tick && m.Tick <= kill.Tick + window);
            if (replies == 0) continue;

            players.TryGetValue(attacker, out var player);
            yield return new Highlight(
                Id: $"{attacker}-chat-{kill.Tick}",
                Type: HighlightType.ChatKill,
                PlayerSteamId: attacker,
                PlayerName: player?.Name ?? kill.AttackerName ?? "Unknown",
                HeroId: player?.HeroId ?? kill.AttackerHeroId ?? 0,
                StartTick: kill.Tick,
                EndTick: kill.Tick,
                StartTimeSeconds: kill.GameTimeSeconds,
                EndTimeSeconds: kill.GameTimeSeconds,
                ClipStartTick: Math.Max(0, kill.Tick - pre),
                ClipEndTick: context.TotalTicks > 0 ? Math.Min(context.TotalTicks, kill.Tick + post) : kill.Tick + post,
                // A victim with more to say ranks a little higher.
                Score: BaseScore + Math.Min(replies - 1, 2) * 0.5,
                Kills: [kill]);
        }
    }

    /// <summary>Drops chat kills that are part of the same player's multikill or takedown streak.</summary>
    public static IEnumerable<Highlight> RemoveCovered(IReadOnlyList<Highlight> highlights)
    {
        var bigger = highlights
            .Where(h => MultiKillDetector.IsMultiKill(h.Type) || h.Type == HighlightType.TakedownStreak)
            .ToList();
        return highlights.Where(h =>
            h.Type != HighlightType.ChatKill ||
            !bigger.Any(b => b.PlayerSteamId == h.PlayerSteamId &&
                             b.Kills.Any(k => k.Tick == h.StartTick && k.VictimSteamId == h.Kills[0].VictimSteamId)));
    }
}
