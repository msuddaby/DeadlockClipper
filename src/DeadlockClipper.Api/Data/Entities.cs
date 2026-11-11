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
}
