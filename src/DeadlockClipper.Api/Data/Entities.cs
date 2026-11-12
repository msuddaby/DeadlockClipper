namespace DeadlockClipper.Api.Data;

public enum DemoSource
{
    Upload,
    LocalReplay,
    MatchId,
}

public enum DemoStatus
{
    // Fetching the replay from Valve (DemoSource.MatchId); then Queued for parsing.
    Downloading,
    Queued,
    Parsing,
    Done,
    Failed,
}

public sealed class Demo
{
    public Guid Id { get; set; }
    public required string FileName { get; set; }

    /// <summary>Absolute path. Uploads live under the data dir; local replays are referenced in place.</summary>
    public required string FilePath { get; set; }

    public DemoSource Source { get; set; }
    public long SizeBytes { get; set; }
    public long? MatchId { get; set; }
    public DemoStatus Status { get; set; }
    public string? Error { get; set; }

    /// <summary>Serialized <see cref="DeadlockClipper.Core.Parsing.MatchAnalysis"/>.</summary>
    public string? AnalysisJson { get; set; }

    // Denormalized for the list view so it doesn't have to deserialize every analysis.
    public string? Map { get; set; }
    public double? DurationSeconds { get; set; }
    public int? HighlightCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ParsedAt { get; set; }

    /// <summary>Analyzer version that produced AnalysisJson; older analyses are re-parsed on startup.</summary>
    public int AnalysisVersion { get; set; }

    /// <summary>Added by auto mode (downloaded or imported); auto mode renders its highlights once parsed.</summary>
    public bool AutoMode { get; set; }

    /// <summary>When auto mode queued this demo's renders (it does so once).</summary>
    public DateTimeOffset? AutoRenderedAt { get; set; }
}

public enum RenderStatus
{
    Queued,
    Rendering,
    Done,
    Failed,
}

/// <summary>A request to render one highlight to a video file.</summary>
public sealed class ClipRender
{
    public Guid Id { get; set; }
    public Guid DemoId { get; set; }
    public required string HighlightId { get; set; }
    public RenderStatus Status { get; set; }
    public string? Error { get; set; }
    public string? OutputPath { get; set; }

    // Denormalized from the highlight so the clips list doesn't need to load analyses.
    public required string PlayerName { get; set; }
    public required string HighlightType { get; set; }
    public int HeroId { get; set; }
    public double GameTimeSeconds { get; set; }
    public long? MatchId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

public enum MetadataStatus
{
    Pending,
    Done,
    // Valve no longer has the metadata for this match.
    Unavailable,
    Failed,
}

/// <summary>
/// A match from a player's history, scored from its metadata (no demo needed) so the best ones can be downloaded.
/// </summary>
public sealed class ScannedMatch
{
    public long MatchId { get; set; }
    /// <summary>The player whose history this came from and whose highlights are scored (SteamID64).</summary>
    public long PlayerSteamId { get; set; }

    public DateTimeOffset StartTime { get; set; }
    public int DurationSeconds { get; set; }
    public int HeroId { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
    public int NetWorth { get; set; }

    public MetadataStatus MetadataStatus { get; set; }
    public string? Error { get; set; }
    /// <summary>Sum of the player's multikill scores; 0 with none. Null until the metadata is in.</summary>
    public double? Score { get; set; }
    /// <summary>Serialized List&lt;MatchHighlightDto&gt;.</summary>
    public string? HighlightsJson { get; set; }
    // Download coordinates from the GC, kept from the metadata scan so downloading the demo costs no GC request.
    public int? ClusterId { get; set; }
    public long? ReplaySalt { get; set; }
    public long? MetadataSalt { get; set; }

    /// <summary>MetadataAnalysis.ScoreVersion that produced Score; older scores are recomputed.</summary>
    public int ScoreVersion { get; set; }

    /// <summary>When Valve deletes the replay (from the Game Coordinator).</summary>
    public DateTimeOffset? ReplayValidThrough { get; set; }
    public DateTimeOffset? ScannedAt { get; set; }
}
