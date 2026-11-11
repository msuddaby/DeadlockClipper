using DeadlockClipper.Core.Highlights;
using DeadlockClipper.Core.Parsing;

namespace DeadlockClipper.Steam;

/// <summary>What a match's metadata says about one player's clip potential, without the demo.</summary>
public sealed record MatchPotential(
    int Kills,
    IReadOnlyList<Highlight> Highlights,
    // Sum of the player's highlight scores (MultiKillDetector's scale), 0 when they have none.
    double Score);

/// <summary>
/// Turns match metadata (every death with killer slot and game time, whole seconds) into the same kill events the
/// demo parser produces, so the highlight detectors can score a match before anything is downloaded.
/// </summary>
public static class MetadataAnalysis
{
    /// <summary>Deadlock demos run at 64 ticks/s. Metadata has no ticks; these are estimates from game time.</summary>
    public const double TickRate = 64;

    private const ulong SteamId64Base = 76561197960265728;

    public static ulong ToSteamId64(uint accountId) => SteamId64Base + accountId;
    public static uint ToAccountId(ulong steamId64) => (uint)(steamId64 & 0xFFFFFFFF);

    public static (IReadOnlyList<PlayerInfo> Players, IReadOnlyList<KillEvent> Kills) ToKills(CMsgMatchMetaDataContents contents)
    {
        var info = contents.MatchInfo;
        var players = info.Players
            .Select(p => new PlayerInfo(
                SteamId: ToSteamId64(p.AccountId),
                Name: "", // metadata carries account ids, not names
                Team: p.Team switch
                {
                    ECitadelLobbyTeam.KEcitadelLobbyTeamTeam0 => Team.Amber,
                    ECitadelLobbyTeam.KEcitadelLobbyTeamTeam1 => Team.Sapphire,
                    _ => Team.Unknown,
                },
                HeroId: (int)p.HeroId,
                Kills: (int)p.Kills,
                Deaths: (int)p.Deaths,
                Assists: (int)p.Assists,
                NetWorth: (int)p.NetWorth))
            .ToList();
        var bySlot = info.Players.ToDictionary(p => p.PlayerSlot);

        var kills = new List<KillEvent>();
        foreach (var victim in info.Players)
        {
            foreach (var death in victim.DeathDetails)
            {
                // Deaths to troopers, guardians etc. have a killer slot that isn't a player.
                bySlot.TryGetValue(death.KillerPlayerSlot, out var killer);
                kills.Add(new KillEvent(
                    Tick: (int)Math.Round(death.GameTimeS * TickRate),
                    GameTimeSeconds: death.GameTimeS,
                    AttackerSteamId: killer is null ? null : ToSteamId64(killer.AccountId),
                    AttackerName: null,
                    AttackerHeroId: killer is null ? null : (int)killer.HeroId,
                    VictimSteamId: ToSteamId64(victim.AccountId),
                    VictimName: "",
                    VictimHeroId: (int)victim.HeroId,
                    Weapon: "",
                    AssisterSteamIds: []));
            }
        }
        kills.Sort((a, b) => a.Tick.CompareTo(b.Tick));
        return (players, kills);
    }

    public static MatchPotential Score(CMsgMatchMetaDataContents contents, ulong playerSteamId)
    {
        var (players, kills) = ToKills(contents);
        var totalTicks = (int)Math.Round(contents.MatchInfo.DurationS * TickRate);
        var highlights = new MultiKillDetector()
            .Detect(new DetectionContext(TickRate, totalTicks, players, kills))
            .Where(h => h.PlayerSteamId == playerSteamId)
            .OrderBy(h => h.StartTick)
            .ToList();
        var playerKills = kills.Count(k => k.AttackerSteamId == playerSteamId && k.VictimSteamId != playerSteamId);
        return new MatchPotential(playerKills, highlights, Math.Round(highlights.Sum(h => h.Score), 2));
    }
}
