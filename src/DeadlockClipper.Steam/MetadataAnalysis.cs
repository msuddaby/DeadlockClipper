using DeadlockClipper.Core.Highlights;
using DeadlockClipper.Core.Parsing;

namespace DeadlockClipper.Steam;

/// <summary>A teamfight the player got kills in, and what it adds to the match's score.</summary>
public sealed record FightPotential(Highlight Fight, int PlayerKills, double Score);

/// <summary>What a match's metadata says about one player's clip potential, without the demo.</summary>
public sealed record MatchPotential(
    int Kills,
    IReadOnlyList<Highlight> Highlights,
    IReadOnlyList<FightPotential> Fights,
    // Multikill scores plus a share of each teamfight the player fought in; 0 when there's nothing.
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

    /// <summary>Bump when scoring changes; stored scores below it are recomputed. v2: teamfights count.</summary>
    public const int ScoreVersion = 2;


    /// <summary>
    /// What a teamfight adds: 1.5 x (player's kills in it)^2, capped at the fight's score. On the multikill scale, so 2
    /// kills in a fight ~ a double kill (6) and 3 ~ a triple (13.5). Scaling by the fight's size instead let one kill
    /// in a 20-kill fight outrank two double kills. Metadata has no assists, so kills are the measure of taking part.
    /// </summary>
    public static double FightScore(Highlight fight, int playerKills) => Math.Round(Math.Min(fight.Score, 1.5 * playerKills * playerKills), 2);

    public static MatchPotential Score(CMsgMatchMetaDataContents contents, ulong playerSteamId)
    {
        var (players, kills) = ToKills(contents);
        var totalTicks = (int)Math.Round(contents.MatchInfo.DurationS * TickRate);
        var context = new DetectionContext(TickRate, totalTicks, players, kills);
        var highlights = new MultiKillDetector()
            .Detect(context)
            .Where(h => h.PlayerSteamId == playerSteamId)
            .OrderBy(h => h.StartTick)
            .ToList();
        var fights = new DenseFightDetector()
            .Detect(context)
            .Select(f => (Fight: f, Mine: f.Kills.Count(k => k.AttackerSteamId == playerSteamId)))
            .Where(x => x.Mine > 0)
            .Select(x => new FightPotential(x.Fight, x.Mine, FightScore(x.Fight, x.Mine)))
            .OrderBy(x => x.Fight.StartTick)
            .ToList();
        var playerKills = kills.Count(k => k.AttackerSteamId == playerSteamId && k.VictimSteamId != playerSteamId);
        return new MatchPotential(playerKills, highlights, fights,
            Math.Round(highlights.Sum(h => h.Score) + fights.Sum(f => f.Score), 2));
    }
}
