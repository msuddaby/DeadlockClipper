namespace DeadlockClipper.Api.Data;

public enum DemoSource
{
    Upload,
    LocalReplay,
    MatchId,
}

public enum DemoStatus
{
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
