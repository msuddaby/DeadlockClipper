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

    [Theory]
    [InlineData("file://{images}/heroes/drifter_card.psd", "panorama/images/heroes/drifter_card_psd.vtex_c")]
    [InlineData("file://{images}/heroes/hero_names/mo_krill.svg", "panorama/images/heroes/hero_names/mo_krill.vsvg_c")]
    [InlineData("file://{images}/hud/icon.png", "panorama/images/hud/icon_png.vtex_c")]
    public void MapsPanoramaPathsToVpkEntries(string panorama, string vpk) => Assert.Equal(vpk, HeroData.VpkPath(panorama));

    [Fact]
    public void ExtractsHeroArtFromLocalInstall()
    {
        if (!File.Exists(Path.Combine(GameDir, "pak01_dir.vpk"))) return;
        var cache = Path.Combine(Path.GetTempPath(), $"hero-assets-{Guid.NewGuid():N}");
        try
        {
            using var assets = new HeroAssets(GameDir, cache);
            var card = assets.Get(64, HeroImage.Card);
            Assert.NotNull(card);
            Assert.EndsWith("drifter_card_psd.png", card);
            Assert.True(new FileInfo(card).Length > 10_000);
            Assert.EndsWith(".svg", assets.Get(64, HeroImage.Logo));
            // Mo & Krill's art is filed as "digger"; paths come from vdata, not from guessing names.
            Assert.Contains("digger_card_gloat", assets.Get(18, HeroImage.CardGloat));
            Assert.Equal(card, assets.Get(64, HeroImage.Card));
        }
        finally
        {
            if (Directory.Exists(cache)) Directory.Delete(cache, true);
        }
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
