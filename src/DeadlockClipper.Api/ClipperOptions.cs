namespace DeadlockClipper.Api;

public sealed class ClipperOptions
{
    public const string Section = "Clipper";

    /// <summary>Where the SQLite db, uploaded demos and caches live. Relative paths resolve from the content root.</summary>
    public string DataDir { get; set; } = "data";

    /// <summary>Deadlock's downloaded-replays folder (game/citadel/replays).</summary>
    public string? ReplaysDir { get; set; }

    /// <summary>Source for hero id → name. Fetched once at startup and cached on disk.</summary>
    public string HeroesUrl { get; set; } = "https://assets.deadlock-api.com/v2/heroes";

    public long MaxUploadBytes { get; set; } = 2L * 1024 * 1024 * 1024;
}
