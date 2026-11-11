using SkiaSharp;
using SteamDatabase.ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;

namespace DeadlockClipper.GameData;

/// <summary>
/// Extracts hero art (portrait cards, the "gloat" card, name logos) from the local Deadlock install into a cache
/// directory as PNG/SVG, so overlays can use real game art. Files are extracted once per game build and reused.
/// Art and fonts are read from the user's own install for their own clips and never redistributed.
/// </summary>
public sealed class HeroAssets : IDisposable
{
    private readonly string _citadelDir;
    private readonly string _cacheDir;
    private readonly Lazy<Package> _pak;
    private readonly Lazy<Dictionary<int, HeroDefinition>> _heroes;
    private readonly Lock _lock = new();

    /// <param name="citadelDir">Deadlock's game/citadel directory.</param>
    /// <param name="cacheRoot">Where extracted files go; a subfolder per game build is used.</param>
    public HeroAssets(string citadelDir, string cacheRoot)
    {
        _citadelDir = citadelDir;
        var dirVpk = Path.Combine(citadelDir, "pak01_dir.vpk");
        // The VPK directory file changes with every game update, so its timestamp identifies the build.
        var build = File.Exists(dirVpk) ? File.GetLastWriteTimeUtc(dirVpk).Ticks.ToString("x") : "none";
        _cacheDir = Path.Combine(cacheRoot, build);
        _pak = new Lazy<Package>(() =>
        {
            var pak = new Package();
            pak.Read(dirVpk);
            return pak;
        });
        _heroes = new Lazy<Dictionary<int, HeroDefinition>>(() => HeroData.Load(_citadelDir).ToDictionary(h => h.Id));
    }

    public string FontsDir => Path.Combine(_citadelDir, "panorama", "fonts");

    public HeroDefinition? Hero(int heroId) => _heroes.Value.GetValueOrDefault(heroId);

    /// <summary>Path to the extracted image file, or null if the hero has no such image in this build.</summary>
    public string? Get(int heroId, HeroImage image)
    {
        if (Hero(heroId)?.Images?.GetValueOrDefault(image) is not { } panoramaPath) return null;
        var vpkPath = HeroData.VpkPath(panoramaPath);
        var isSvg = vpkPath.EndsWith(".vsvg_c", StringComparison.Ordinal);
        var target = Path.Combine(_cacheDir, Path.GetFileNameWithoutExtension(vpkPath) + (isSvg ? ".svg" : ".png"));
        if (File.Exists(target)) return target;

        lock (_lock)
        {
            if (File.Exists(target)) return target;
            var entry = _pak.Value.FindEntry(vpkPath);
            if (entry == null) return null;
            _pak.Value.ReadEntry(entry, out var bytes);
            using var resource = new Resource();
            resource.Read(new MemoryStream(bytes));

            Directory.CreateDirectory(_cacheDir);
            var tmp = target + ".tmp";
            switch (resource.DataBlock)
            {
                case Texture texture:
                    using (var bitmap = texture.GenerateBitmap())
                    using (var png = bitmap.Encode(SKEncodedImageFormat.Png, 100))
                    using (var file = File.Create(tmp))
                        png.SaveTo(file);
                    break;
                case Panorama svg:
                    File.WriteAllBytes(tmp, svg.Data);
                    break;
                default:
                    return null;
            }
            File.Move(tmp, target, overwrite: true);
            return target;
        }
    }

    public void Dispose()
    {
        if (_pak.IsValueCreated) _pak.Value.Dispose();
    }
}
