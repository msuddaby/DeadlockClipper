using System.Collections.Concurrent;
using System.Text.Json;
using DeadlockClipper.Api.Data;
using DeadlockClipper.Core.Parsing;

namespace DeadlockClipper.Api;

public sealed record OverviewPlayerDto(ulong SteamId, string Name, int HeroId, Team Team, int Kills, int Deaths, int Assists, int NetWorth);

/// <param name="Participants">For teamfights: everyone who killed, died or assisted.</param>
public sealed record OverviewHighlightDto(string Type, ulong PlayerSteamId, double TimeSeconds, int Kills, IReadOnlyList<ulong> Participants);

/// <summary>Enough of a parsed match for the demos list: who played what, who won, and the highlights.</summary>
public sealed record DemoOverviewDto(
    Team WinningTeam,
    int HeroKills,
    IReadOnlyList<OverviewPlayerDto> Players,
    IReadOnlyList<OverviewHighlightDto> Highlights);

/// <summary>Builds overviews from stored analyses; cached per parse so the list doesn't re-read every analysis.</summary>
public static class DemoOverview
{
    private static readonly ConcurrentDictionary<(Guid, DateTimeOffset?), DemoOverviewDto> Cache = new();

    public static DemoOverviewDto? For(Demo d, JsonSerializerOptions json)
    {
        if (d.Status != DemoStatus.Done || d.AnalysisJson == null) return null;
        return Cache.GetOrAdd((d.Id, d.ParsedAt), _ => Build(JsonSerializer.Deserialize<MatchAnalysis>(d.AnalysisJson, json)!));
    }

    internal static DemoOverviewDto Build(MatchAnalysis a) => new(
        a.WinningTeam,
        a.Kills.Count(k => k.AttackerSteamId is { } x && x != 0 && x != k.VictimSteamId),
        a.Players.Select(p => new OverviewPlayerDto(p.SteamId, p.Name, p.HeroId, p.Team, p.Kills, p.Deaths, p.Assists, p.NetWorth)).ToList(),
        a.Highlights.OrderBy(h => h.StartTick).Select(h => new OverviewHighlightDto(
            h.Type.ToString(), h.PlayerSteamId, h.StartTimeSeconds, h.Kills.Count,
            h.Type == HighlightType.TeamFight
                ? h.Kills.SelectMany(k => k.AssisterSteamIds.Append(k.VictimSteamId).Append(k.AttackerSteamId ?? 0)).Where(x => x != 0).Distinct().ToList()
                : [])).ToList());
}
