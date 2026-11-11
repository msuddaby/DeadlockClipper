namespace DeadlockClipper.Core.Parsing;

/// <summary>
/// Teams as Deadlock networks them (CitadelTeamNum). In game, Amber is the Hidden King's team and Sapphire the
/// Archmother's; the code keeps the network names.
/// </summary>
public enum Team
{
    Unknown = 0,
    Spectator = 1,
    Amber = 2,
    Sapphire = 3,
}

public sealed record PlayerInfo(
    ulong SteamId,
    string Name,
    Team Team,
    int HeroId,
    int Kills,
    int Deaths,
    int Assists,
    int NetWorth,
    // Lane the player started in (Deadlock numbers them 1, 4, 6).
    int Lane = 0,
    // Controller entity index, used to break ties within a lane.
    int EntityIndex = 0,
    // Position in the in-game top bar, left to right: Amber 1-6, Sapphire 7-12. 0 if unknown.
    // This is what `citadel_spectate_topbar_player` takes.
    int TopBarSlot = 0);

public sealed record KillEvent(
    int Tick,
    double GameTimeSeconds,
    ulong? AttackerSteamId,
    string? AttackerName,
    int? AttackerHeroId,
    ulong VictimSteamId,
    string VictimName,
    int VictimHeroId,
    string Weapon,
    IReadOnlyList<ulong> AssisterSteamIds);

public enum HighlightType
{
    DoubleKill,
    TripleKill,
    QuadKill,
    PentaKill,
    MegaKill, // 6+
    // All of one player's kills spliced together; see Highlights.KillCompilation.
    KillCompilation,
    // A burst of kills from both teams with the quiet stretches cut out; see Highlights.DenseFightDetector.
    TeamFight,
}

/// <summary>A stretch of demo to record, in ticks.</summary>
/// <param name="FocusSteamId">Player the camera follows during this segment; null: the highlight's player.</param>
public sealed record ClipSegment(int StartTick, int EndTick, ulong? FocusSteamId = null);

public sealed record Highlight(
    // Stable within a demo: "{playerSteamId}-{startTick}".
    string Id,
    HighlightType Type,
    ulong PlayerSteamId,
    string PlayerName,
    int HeroId,
    int StartTick,
    int EndTick,
    double StartTimeSeconds,
    double EndTimeSeconds,
    // Padded window a renderer should record, in demo ticks.
    int ClipStartTick,
    int ClipEndTick,
    double Score,
    IReadOnlyList<KillEvent> Kills,
    // Set when the clip is several pieces of the demo joined together; ClipStartTick/ClipEndTick then span all of them.
    IReadOnlyList<ClipSegment>? Segments = null);

public sealed record MatchAnalysis(
    long? MatchId,
    string Map,
    int BuildNumber,
    double TickRate,
    int TotalTicks,
    double DurationSeconds,
    IReadOnlyList<PlayerInfo> Players,
    IReadOnlyList<KillEvent> Kills,
    IReadOnlyList<Highlight> Highlights,
    // From the GameOver message; Unknown for demos that end before the Patron falls (or older analyses).
    Team WinningTeam = Team.Unknown);
