using System.Text.Json;
using DeadlockClipper.Api.Storage;
using Microsoft.Extensions.Options;

namespace DeadlockClipper.Api.Heroes;

public sealed record HeroInfo(int Id, string Name, string? IconUrl);

/// <summary>
/// Hero id → name/icon. Demos only carry numeric hero ids, so names come from the community assets API,
/// cached to disk so the app still works offline after the first successful fetch.
/// </summary>
public sealed class HeroCatalog(
    IHttpClientFactory http,
    IOptions<ClipperOptions> options,
    DemoStorage storage,
    ILogger<HeroCatalog> log) : BackgroundService
{
    private volatile IReadOnlyDictionary<int, HeroInfo> _heroes = new Dictionary<int, HeroInfo>();
    private string CachePath => Path.Combine(storage.DataDir, "heroes.json");

    public IReadOnlyDictionary<int, HeroInfo> All => _heroes;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (File.Exists(CachePath))
        {
            try
            {
                _heroes = Parse(await File.ReadAllTextAsync(CachePath, ct));
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Ignoring unreadable hero cache");
            }
        }

        try
        {
            var client = http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            var body = await client.GetStringAsync(options.Value.HeroesUrl, ct);
            var parsed = Parse(body);
            if (parsed.Count > 0)
            {
                _heroes = parsed;
                await File.WriteAllTextAsync(CachePath, body, ct);
                log.LogInformation("Loaded {Count} heroes", parsed.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning("Could not fetch hero names from {Url} ({Message}); using {Count} cached heroes",
                options.Value.HeroesUrl, ex.Message, _heroes.Count);
        }
    }

    private static Dictionary<int, HeroInfo> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<int, HeroInfo>();
        foreach (var hero in doc.RootElement.EnumerateArray())
        {
            if (!hero.TryGetProperty("id", out var id) || !hero.TryGetProperty("name", out var name)) continue;
            string? icon = null;
            if (hero.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "icon_image_small", "icon_hero_card", "minimap_image" })
                {
                    if (images.TryGetProperty(key, out var url) && url.ValueKind == JsonValueKind.String)
                    {
                        icon = url.GetString();
                        break;
                    }
                }
            }
            result[id.GetInt32()] = new HeroInfo(id.GetInt32(), name.GetString() ?? $"Hero #{id}", icon);
        }
        return result;
    }
}
