using DeadlockClipper.Core.Parsing;

namespace DeadlockClipper.Core.Tests;

public class TopBarSlotTests
{
    private static PlayerInfo P(string name, Team team, int lane, int entity) =>
        new((ulong)entity, name, team, 1, 0, 0, 0, 0, Lane: lane, EntityIndex: entity);

    [Fact]
    public void MatchesTopBarObservedInMatch135555232()
    {
        // Lane/entity values parsed from the demo; expected slots read off the game's top bar.
        var players = new[]
        {
            P("B RADiical", Team.Amber, 6, 2), P("The Den", Team.Amber, 4, 3), P("BigSerg", Team.Amber, 6, 6),
            P("Soyah", Team.Amber, 4, 7), P("S-Fang55", Team.Amber, 1, 9), P("FunniHeCool", Team.Amber, 1, 12),
            P("notthebaas", Team.Sapphire, 1, 1), P("Cthaeh", Team.Sapphire, 1, 4), P("silly.goose", Team.Sapphire, 6, 5),
            P("Pancakes", Team.Sapphire, 4, 8), P("its cap", Team.Sapphire, 4, 10), P("CSG", Team.Sapphire, 6, 11),
        };

        var slots = DemoAnalyzer.AssignTopBarSlots(players).ToDictionary(p => p.Name, p => p.TopBarSlot);

        Assert.Equal(
            ["S-Fang55", "FunniHeCool", "The Den", "Soyah", "B RADiical", "BigSerg",
             "notthebaas", "Cthaeh", "Pancakes", "its cap", "silly.goose", "CSG"],
            slots.OrderBy(kv => kv.Value).Select(kv => kv.Key));
    }

    [Fact]
    public void SpectatorsAndExtraPlayersGetNoSlot()
    {
        var players = Enumerable.Range(1, 7).Select(i => P($"a{i}", Team.Amber, 1, i))
            .Append(P("spec", Team.Spectator, 0, 50));

        var result = DemoAnalyzer.AssignTopBarSlots(players).ToList();

        Assert.Equal(0, result.Single(p => p.Name == "spec").TopBarSlot);
        Assert.Equal(0, result.Single(p => p.Name == "a7").TopBarSlot);
        Assert.Equal(Enumerable.Range(1, 6), result.Where(p => p.TopBarSlot > 0).Select(p => p.TopBarSlot).Order());
    }
}
