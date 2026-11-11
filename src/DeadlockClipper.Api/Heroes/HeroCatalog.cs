using System.Text.Json;
using DeadlockClipper.Api.Storage;
using DeadlockClipper.GameData;
using DeadlockClipper.Render;
using Microsoft.Extensions.Options;

namespace DeadlockClipper.Api.Heroes;

public sealed record HeroInfo(int Id, string Name, string? IconUrl);

/// <summary>
/// Hero id → name. Demos only carry numeric ids. Names are read from the local Deadlock install (offline, always
/// matches the installed game version); the community assets API is only a fallback when the game isn't installed.
/// The last good result is cached on disk.
/// </summary>
public sealed class HeroCatalog(
    IHttpClientFactory http,
    IOptions<ClipperOptions> options,
    RenderOptions render,
    DemoStorage storage,
    ILogger<HeroCatalog> log) : BackgroundService
{
    private volatile IReadOnlyDictionary<int, HeroInfo> _heroes = new Dictionary<int, HeroInfo>();
    private string CachePath => Path.Combine(storage.DataDir, "heroes-cache.json");

    public IReadOnlyDictionary<int, HeroInfo> All => _heroes;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await LoadCacheAsync(ct);

        if (await TryLoadFromGameAsync(ct) || await TryLoadFromApiAsync(ct))
        {
            await File.WriteAllTextAsync(CachePath, JsonSerializer.Serialize(_heroes.Values), ct);
            return;
        }
        log.LogWarning("Hero names unavailable from game files or {Url}; using {Count} cached heroes",
            options.Value.HeroesUrl, _heroes.Count);
    }

    private async Task<bool> TryLoadFromGameAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(render.GameDir) || !File.Exists(Path.Combine(render.GameDir, "pak01_dir.vpk")))
            return false;
        try
        {
            // Reading the VPK is synchronous and takes a moment; keep it off the startup path.
            var heroes = await Task.Run(() => HeroData.Load(render.GameDir), ct);
            _heroes = heroes.ToDictionary(h => h.Id, h => new HeroInfo(h.Id, h.Name, null));
            log.LogInformation("Loaded {Count} heroes from the Deadlock install", heroes.Count);
            return heroes.Count > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not read hero data from {GameDir}", render.GameDir);
            return false;
        }
    }

    private async Task<bool> TryLoadFromApiAsync(CancellationToken ct)
    {
        try
        {
            var client = http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            var parsed = ParseAssetsApi(await client.GetStringAsync(options.Value.HeroesUrl, ct));
            if (parsed.Count == 0) return false;
            _heroes = parsed;
            log.LogInformation("Loaded {Count} heroes from {Url}", parsed.Count, options.Value.HeroesUrl);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogDebug(ex, "Hero API unavailable");
            return false;
        }
    }

    private async Task LoadCacheAsync(CancellationToken ct)
    {
        if (!File.Exists(CachePath)) return;
        try
        {
            var cached = JsonSerializer.Deserialize<List<HeroInfo>>(await File.ReadAllTextAsync(CachePath, ct));
            if (cached is { Count: > 0 }) _heroes = cached.ToDictionary(h => h.Id);
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "Ignoring unreadable hero cache");
        }
    }

    private static Dictionary<int, HeroInfo> ParseAssetsApi(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<int, HeroInfo>();
        foreach (var hero in doc.RootElement.EnumerateArray())
        {
            if (!hero.TryGetProperty("id", out var id) || !hero.TryGetProperty("name", out var name)) continue;
            string? icon = null;
            if (hero.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Object
                && images.TryGetProperty("icon_image_small", out var url) && url.ValueKind == JsonValueKind.String)
                icon = url.GetString();
            result[id.GetInt32()] = new HeroInfo(id.GetInt32(), name.GetString() ?? $"Hero #{id}", icon);
        }
        return result;
    }
}
