using DeadlockClipper.Core.Highlights;
using DemoFile;
using DemoFile.Game.Deadlock;

namespace DeadlockClipper.Core.Parsing;

/// <summary>Parses a Deadlock .dem in a single pass and runs highlight detectors over the result.</summary>
public sealed class DemoAnalyzer(IEnumerable<IHighlightDetector>? detectors = null)
{
    /// <summary>
    /// Bump when MatchAnalysis gains data that stored analyses need; older ones are re-parsed on startup.
    /// v2: top-bar slots, highlight ids. v3: teamfight highlights. v4: winning team. v5: takedown streaks. v6: chat. v7: chat kills.
    /// </summary>
    public const int Version = 7;

    private const double DefaultTickRate = 64;
    private readonly IReadOnlyList<IHighlightDetector> _detectors = detectors?.ToList() ?? [new MultiKillDetector(), new DenseFightDetector(), new TakedownDetector(), new ChatKillDetector()];

    public async Task<MatchAnalysis> AnalyzeFileAsync(string path, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        return await AnalyzeAsync(file, progress, ct);
    }

    public async Task<MatchAnalysis> AnalyzeAsync(Stream stream, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var demo = new DeadlockDemoParser();
        var tickRate = DefaultTickRate;
        var players = new Dictionary<ulong, PlayerInfo>();
        var kills = new List<KillEvent>();

        demo.PacketEvents.SvcServerInfo += e =>
        {
            if (e.TickInterval > 0) tickRate = 1.0 / e.TickInterval;
        };

        demo.Source1GameEvents.PlayerDeath += e =>
        {
            var victim = e.Player;
            if (victim is null || victim.SteamID == 0) return; // troopers, guardians, etc.

            var attacker = e.Attacker;
            var attackerIsPlayer = attacker is { SteamID: not 0 };

            Snapshot(victim);
            if (attackerIsPlayer) Snapshot(attacker!);

            kills.Add(new KillEvent(
                Tick: demo.CurrentDemoTick.Value,
                GameTimeSeconds: MatchClock(demo),
                AttackerSteamId: attackerIsPlayer ? attacker!.SteamID : null,
                AttackerName: attackerIsPlayer ? attacker!.PlayerName : NullIfEmpty(e.Attackername),
                AttackerHeroId: attackerIsPlayer ? HeroOf(attacker!) : null,
                VictimSteamId: victim.SteamID,
                VictimName: victim.PlayerName,
                VictimHeroId: HeroOf(victim),
                Weapon: e.Weapon ?? "",
                AssisterSteamIds: new[] { e.Assister1controller, e.Assister2controller, e.Assister3controller, e.Assister4controller, e.Assister5controller }
                    .Where(a => a is { SteamID: not 0 } && a.SteamID != attacker?.SteamID)
                    .Select(a => a!.SteamID)
                    .Distinct()
                    .ToArray()));
        };

        // Chat names its sender by player slot: the controller's entity index minus one (players are entities 1-12).
        // Checked on match 135555232: a player killed at 21:42 apologizes to their team at 21:48. (Chat-wheel messages
        // would be a better check, but their fields don't decode with these protos: account id 0, slots like 137.)
        var chat = new List<ChatMessage>();
        demo.UserMessageEvents.ChatMsg += m =>
        {
            if (string.IsNullOrWhiteSpace(m.Text)) return;
            var sender = SenderOf(m.PlayerSlot);
            chat.Add(new ChatMessage(demo.CurrentDemoTick.Value, MatchClock(demo), sender?.SteamID is > 0 ? sender.SteamID : null,
                sender?.PlayerName ?? "", m.AllChat, m.Text.Trim()));
        };
        CCitadelPlayerController? SenderOf(int slot) =>
            slot < 0 ? null : demo.PlayersIncludingDisconnected.FirstOrDefault(c => (int)c.EntityIndex.Value == slot + 1);

        // Sent once when the Patron falls; winning_team is a CitadelTeamNum (2/3), checked against real matches.
        int? winningTeam = null;
        demo.UserMessageEvents.GameOver += e => winningTeam = e.WinningTeam;

        long? matchId = null;
        var reader = DemoFileReader.Create(demo, progress is null ? stream : new ProgressStream(stream, progress));
        await reader.ReadAllAsync(ct);

        // Final scoreboard state.
        foreach (var controller in demo.PlayersIncludingDisconnected) Snapshot(controller);
        if (demo.GameRules is { } rules && rules.MatchID.Value != 0) matchId = (long)rules.MatchID.Value;

        var totalTicks = demo.CurrentDemoTick.Value;
        var playerList = AssignTopBarSlots(players.Values.Where(p => p.Team is Team.Amber or Team.Sapphire))
            .OrderBy(p => p.Team).ThenByDescending(p => p.NetWorth)
            .ToList();

        var context = new DetectionContext(tickRate, totalTicks, playerList, kills, chat);
        var detected = TakedownDetector.RemoveCoveredMultiKills(_detectors.SelectMany(d => d.Detect(context)).ToList()).ToList();
        var highlights = ChatKillDetector.RemoveCovered(detected)
            .OrderByDescending(h => h.Score).ThenBy(h => h.StartTick)
            .ToList();

        progress?.Report(1);

        return new MatchAnalysis(
            MatchId: matchId,
            Map: demo.ServerInfo?.MapName ?? demo.FileHeader?.MapName ?? "",
            BuildNumber: demo.FileHeader?.BuildNum ?? 0,
            TickRate: tickRate,
            TotalTicks: totalTicks,
            DurationSeconds: MatchClock(demo),
            Players: playerList,
            Kills: kills,
            Highlights: highlights,
            WinningTeam: winningTeam is 2 or 3 ? (Team)winningTeam.Value : Team.Unknown,
            Chat: chat);

        void Snapshot(CCitadelPlayerController c)
        {
            if (c.SteamID == 0) return;
            var data = c.PlayerDataGlobal;
            var previous = players.GetValueOrDefault(c.SteamID);
            var team = (Team)c.TeamNum;
            players[c.SteamID] = new PlayerInfo(
                SteamId: c.SteamID,
                Name: string.IsNullOrEmpty(c.PlayerName) ? previous?.Name ?? "" : c.PlayerName,
                // Late in a demo controllers can drop to team 0 on disconnect; keep the last real team.
                Team: team is Team.Amber or Team.Sapphire ? team : previous?.Team ?? team,
                HeroId: data.HeroID.Value != 0 ? data.HeroID.Value : previous?.HeroId ?? 0,
                Kills: Math.Max(data.PlayerKills, previous?.Kills ?? 0),
                Deaths: Math.Max(data.Deaths, previous?.Deaths ?? 0),
                Assists: Math.Max(data.PlayerAssists, previous?.Assists ?? 0),
                NetWorth: Math.Max(data.GoldNetWorth, previous?.NetWorth ?? 0),
                Lane: c.OriginalLaneAssignment != 0 ? c.OriginalLaneAssignment : previous?.Lane ?? 0,
                EntityIndex: (int)c.EntityIndex.Value);
        }
    }

    /// <summary>
    /// The in-game top bar lists each team by starting lane (1, 4, 6), then by controller entity index:
    /// Amber fills slots 1-6, Sapphire 7-12. Verified against a live replay for all 12 players.
    /// Uses the original lane; if lane swaps reorder the bar mid-match this may need AssignedLane instead.
    /// </summary>
    public static IEnumerable<PlayerInfo> AssignTopBarSlots(IEnumerable<PlayerInfo> players)
    {
        foreach (var team in players.GroupBy(p => p.Team))
        {
            var offset = team.Key switch { Team.Amber => 0, Team.Sapphire => 6, _ => -1 };
            var ordered = team.OrderBy(p => p.Lane).ThenBy(p => p.EntityIndex).ToList();
            for (var i = 0; i < ordered.Count; i++)
                yield return ordered[i] with { TopBarSlot = offset >= 0 && i < 6 ? offset + i + 1 : 0 };
        }
    }

    private static int HeroOf(CCitadelPlayerController c) => c.PlayerDataGlobal.HeroID.Value;

    /// <summary>Seconds since the match clock started (0 before the game starts).</summary>
    private static double MatchClock(DeadlockDemoParser demo)
    {
        var now = demo.CurrentGameTime.Value;
        var start = demo.GameRules?.GameStartTime.Value ?? 0;
        return Math.Round(Math.Max(0, start > 0 ? now - start : now), 2);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
