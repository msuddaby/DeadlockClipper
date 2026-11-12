using DeadlockClipper.Core.Parsing;

namespace DeadlockClipper.Core.Highlights;

public sealed record TakedownOptions
{
    /// <summary>Maximum gap between consecutive takedowns (kills or assists) to keep the streak going.</summary>
    public double MaxGapSeconds { get; init; } = 10;

    /// <summary>Takedowns needed. With a 10 s gap, 3+ finds ~1-4 plays per match on real demos.</summary>
    public int MinTakedowns { get; init; } = 3;

    public double PrePaddingSeconds { get; init; } = 5;
    public double PostPaddingSeconds { get; init; } = 2;
}

/// <summary>
/// Plays a player made happen without getting every kill: a Dynamo ult that lets the team clean up, a stun into a
/// teammate's burst. Chains the kills a player took part in (as killer or assister) like <see cref="MultiKillDetector"/>
/// chains kills; a streak needs at least one assist, since all-kill streaks are multikills already.
/// </summary>
public sealed class TakedownDetector(TakedownOptions? options = null) : IHighlightDetector
{
    private readonly TakedownOptions _options = options ?? new TakedownOptions();

    public IEnumerable<Highlight> Detect(DetectionContext context)
    {
        var maxGap = _options.MaxGapSeconds * context.TickRate;
        var pre = (int)Math.Round(_options.PrePaddingSeconds * context.TickRate);
        var post = (int)Math.Round(_options.PostPaddingSeconds * context.TickRate);
        var kills = context.Kills.Where(k => k.AttackerSteamId != k.VictimSteamId).OrderBy(k => k.Tick).ToList();

        foreach (var player in context.Players)
        {
            var id = player.SteamId;
            var streak = new List<KillEvent>();
            foreach (var k in kills.Where(k => k.AttackerSteamId == id || k.AssisterSteamIds.Contains(id)))
            {
                if (streak.Count > 0 && k.Tick - streak[^1].Tick > maxGap)
                {
                    if (Build(player, streak) is { } h) yield return h;
                    streak = [];
                }
                streak.Add(k);
            }
            if (Build(player, streak) is { } last) yield return last;
        }

        Highlight? Build(PlayerInfo player, List<KillEvent> streak)
        {
            if (streak.Count < _options.MinTakedowns) return null;
            var ownKills = streak.Count(k => k.AttackerSteamId == player.SteamId);
            if (ownKills == streak.Count) return null; // a pure multikill

            var first = streak[0];
            var final = streak[^1];
            var span = (final.Tick - first.Tick) / context.TickRate;
            return new Highlight(
                Id: $"{player.SteamId}-td-{first.Tick}",
                Type: HighlightType.TakedownStreak,
                PlayerSteamId: player.SteamId,
                PlayerName: player.Name,
                HeroId: player.HeroId,
                StartTick: first.Tick,
                EndTick: final.Tick,
                StartTimeSeconds: first.GameTimeSeconds,
                EndTimeSeconds: final.GameTimeSeconds,
                ClipStartTick: Math.Max(0, first.Tick - pre),
                ClipEndTick: context.TotalTicks > 0 ? Math.Min(context.TotalTicks, final.Tick + post) : final.Tick + post,
                // Multikill scoring with assists counting 75%: an all-assist triple ranks above a double kill and below
                // a triple kill.
                Score: Math.Round(Math.Pow(ownKills + 0.75 * (streak.Count - ownKills), 2) * (1 + 1.0 / (1.0 + span / 10.0)), 2),
                Kills: streak.ToArray());
        }
    }

    /// <summary>
    /// Drops multikills that sit entirely inside the same player's takedown streak: the streak is the bigger play and
    /// clips the same moment.
    /// </summary>
    public static IEnumerable<Highlight> RemoveCoveredMultiKills(IReadOnlyList<Highlight> highlights)
    {
        var streaks = highlights.Where(h => h.Type == HighlightType.TakedownStreak).ToList();
        return highlights.Where(h =>
            !MultiKillDetector.IsMultiKill(h.Type) ||
            !streaks.Any(s => s.PlayerSteamId == h.PlayerSteamId && s.StartTick <= h.StartTick && s.EndTick >= h.EndTick));
    }

    /// <summary>"TRIPLE TAKEDOWN", "QUAD TAKEDOWN", …</summary>
    public static string Label(int takedowns) => takedowns switch
    {
        <= 3 => "TRIPLE TAKEDOWN",
        4 => "QUAD TAKEDOWN",
        5 => "PENTA TAKEDOWN",
        _ => "MEGA TAKEDOWN",
    };
}
