using DeadlockClipper.GameData;

namespace DeadlockClipper.Render.Tests;

public class HeroDataTests
{
    private const string GameDir = "/mnt/gamedrive/SteamLibrary/steamapps/common/Deadlock/game/citadel";

    [Fact]
    public void ParsesDisplayNameTokensOnly()
    {
        const string text = """
            "lang" { "Tokens" {
                "hero_inferno:n" "Infernus"
                "hero_inferno_search:n" "Infernus"
                "hero_inferno_lore"  "Like most teenagers..."
                "hero_magician:n" "Sinclair"
            } }
            """;
        Assert.Equal([("hero_inferno", "Infernus"), ("hero_magician", "Sinclair")], HeroData.ParseHeroNameTokens(text));
    }

    /// <summary>Reads the real install when present (hero 64 = Drifter, confirmed by the user).</summary>
    [Fact]
    public void LoadsHeroesFromLocalInstall()
    {
        if (!File.Exists(Path.Combine(GameDir, "pak01_dir.vpk"))) return;

        var heroes = HeroData.Load(GameDir).ToDictionary(h => h.Id);

        Assert.Equal("Drifter", heroes[64].Name);
        Assert.Equal("Infernus", heroes[1].Name);
        Assert.Equal("Seven", heroes[2].Name);
        Assert.Equal("Sinclair", heroes[60].Name);
        Assert.True(heroes[64].Playable);
        Assert.True(heroes.Values.Count(h => h.Playable) >= 30);
    }
}
