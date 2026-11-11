using System.Text.RegularExpressions;
using SteamDatabase.ValvePak;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace DeadlockClipper.GameData;

/// <param name="Id">Numeric hero id as networked in demos (PlayerDataGlobal.HeroID).</param>
/// <param name="Key">Internal name, e.g. "hero_drifter".</param>
/// <param name="Name">Localized display name, e.g. "Drifter".</param>
public sealed record HeroDefinition(int Id, string Key, string Name, bool Playable);

/// <summary>
/// Reads hero definitions straight from the local Deadlock install, so names work offline and follow game patches:
/// ids come from scripts/heroes.vdata_c in pak01_dir.vpk, display names from the English localization files.
/// </summary>
public static partial class HeroData
{
    /// <param name="citadelDir">Deadlock's game/citadel directory.</param>
    public static IReadOnlyList<HeroDefinition> Load(string citadelDir)
    {
        var names = LoadLocalizedNames(Path.Combine(citadelDir, "resource", "localization"));

        using var pak = new Package();
        pak.Read(Path.Combine(citadelDir, "pak01_dir.vpk"));
        var entry = pak.FindEntry("scripts/heroes.vdata_c")
                    ?? throw new InvalidDataException("scripts/heroes.vdata_c not found in pak01_dir.vpk");
        pak.ReadEntry(entry, out var bytes);

        using var resource = new Resource();
        resource.Read(new MemoryStream(bytes));
        if (resource.DataBlock is not BinaryKV3 kv3) throw new InvalidDataException("heroes.vdata_c is not KV3 data");

        var heroes = new List<HeroDefinition>();
        foreach (var (key, hero) in (KVObject)kv3.Data)
        {
            if (!key.StartsWith("hero_", StringComparison.Ordinal) || !hero.TryGetValue("m_HeroID", out var id)) continue;
            var heroId = id.ToInt32();
            if (heroId <= 0) continue; // hero_base

            var playable = !Flag(hero, "m_bDisabled") && !Flag(hero, "m_bInDevelopment");
            heroes.Add(new HeroDefinition(heroId, key, names.GetValueOrDefault(key) ?? FallbackName(key), playable));
        }
        return heroes;
    }

    /// <summary>Collects "hero_x:n" "Display Name" tokens from every *_english.txt localization file.</summary>
    public static Dictionary<string, string> LoadLocalizedNames(string localizationDir)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(localizationDir)) return names;
        foreach (var file in Directory.EnumerateFiles(localizationDir, "*_english.txt", SearchOption.AllDirectories))
            foreach (var (key, name) in ParseHeroNameTokens(File.ReadAllText(file)))
                names.TryAdd(key, name);
        return names;
    }

    public static IEnumerable<(string Key, string Name)> ParseHeroNameTokens(string localizationText)
    {
        foreach (Match m in NameToken().Matches(localizationText))
            yield return (m.Groups["key"].Value, m.Groups["name"].Value);
    }

    // "hero_drifter:n" "Drifter"  (":n" marks the display name; _search/_sort variants are excluded by the key pattern)
    [GeneratedRegex("\"(?<key>hero_[a-z0-9]+):n\"\\s+\"(?<name>[^\"]+)\"")]
    private static partial Regex NameToken();

    private static bool Flag(KVObject hero, string name) => hero.TryGetValue(name, out var v) && v.ToBoolean();

    private static string FallbackName(string key)
    {
        var raw = key["hero_".Length..];
        return raw.Length == 0 ? key : char.ToUpperInvariant(raw[0]) + raw[1..];
    }
}
