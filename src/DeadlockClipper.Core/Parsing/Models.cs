namespace DeadlockClipper.Core.Parsing;

/// <summary>Teams as Deadlock networks them (CitadelTeamNum).</summary>
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
    int NetWorth);

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
}

public sealed record Highlight(
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
    IReadOnlyList<KillEvent> Kills);

public sealed record MatchAnalysis(
    long? MatchId,
    string Map,
    int BuildNumber,
    double TickRate,
    int TotalTicks,
    double DurationSeconds,
    IReadOnlyList<PlayerInfo> Players,
    IReadOnlyList<KillEvent> Kills,
    IReadOnlyList<Highlight> Highlights);
