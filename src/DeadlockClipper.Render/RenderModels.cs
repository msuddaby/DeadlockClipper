namespace DeadlockClipper.Render;

/// <summary>Configuration for the local render pipeline. Bound from the "Clipper:Render" config section.</summary>
public sealed class RenderOptions
{
    /// <summary>Deadlock's game/citadel directory (contains cfg/, replays/, console.log).</summary>
    public string GameDir { get; set; } = "";

    public int SteamAppId { get; set; } = 1422450;
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public int Fps { get; set; } = 60;

    /// <summary>Long demo_gototick jumps crash the client; seek in hops of at most this many ticks.</summary>
    public int SeekStepTicks { get; set; } = 20000;
    public double HopSettleSeconds { get; set; } = 6;
    public double DemoSettleSeconds { get; set; } = 10;
    public double LaunchTimeoutSeconds { get; set; } = 120;

    /// <summary>GStreamer H.264 encoder element; vah264enc for AMD/Intel VA-API, x264enc for CPU.</summary>
    public string VideoEncoder { get; set; } = "vah264enc";
    public int VideoBitrateKbps { get; set; } = 12000;

    /// <summary>Where finished clips are written.</summary>
    public string OutputDir { get; set; } = "";
    /// <summary>Scratch space for raw recordings; cleaned per job.</summary>
    public string WorkDir { get; set; } = "";

    /// <summary>Null sink the game's audio is moved to so nothing reaches the speakers.</summary>
    public string SilentSink { get; set; } = "deadlock_clipper_silent";

    /// <summary>Headless browser used to render the styled overlay graphics. Without it, plain text captions are used.</summary>
    public string ChromiumPath { get; set; } = "chromium";
    /// <summary>"styled" (hero art, animated kill markers) or "plain" (text only).</summary>
    public string CaptionStyle { get; set; } = "styled";
    /// <summary>Where extracted hero art is cached.</summary>
    public string AssetCacheDir { get; set; } = "";
}

/// <summary>A kill within the clip, at a time relative to the clip start (the death event tick).</summary>
public sealed record KillMark(double OffsetSeconds, string Label, bool IsFinal, string VictimName = "", int VictimHeroId = 0);

/// <summary>Everything needed to render one highlight. Plain data so it can be sent to a remote worker later.</summary>
public sealed record RenderJob(
    string JobId,
    string DemoKey,
    string DemoPath,
    string HighlightId,
    int StartTick,
    int EndTick,
    double TickRate,
    int TopBarSlot,
    string IntroTitle,
    string IntroSubtitle,
    IReadOnlyList<KillMark> KillMarks,
    string OutputPath,
    string PlayerName = "",
    int PlayerHeroId = 0,
    // "DOUBLE KILL", "TRIPLE KILL", …
    string HighlightLabel = "")
{
    public double DurationSeconds => (EndTick - StartTick) / TickRate;
}

public enum RenderStage
{
    Queued,
    Launching,
    LoadingDemo,
    Seeking,
    Recording,
    Encoding,
    Done,
    Failed,
}

public sealed record RenderProgress(string JobId, RenderStage Stage, string Detail = "");

public sealed record RenderResult(string JobId, bool Success, string? OutputPath, string? Error);
