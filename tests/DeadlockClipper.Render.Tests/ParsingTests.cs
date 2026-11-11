using DeadlockClipper.Render;

namespace DeadlockClipper.Render.Tests;

public class ParsingTests
{
    // Real ffmpeg output (tblend difference + signalstats) from a render: the animated "GAME IS PAUSED" box gives
    // small differences until playback actually starts at 0.902 s.
    private const string PausedThenPlaying = """
        [Parsed_metadata_2 @ 0x1] frame:0    pts:0       pts_time:0.002
        [Parsed_metadata_2 @ 0x1] lavfi.signalstats.YAVG=0.458715
        [Parsed_metadata_2 @ 0x1] frame:27   pts:1404    pts_time:0.469
        [Parsed_metadata_2 @ 0x1] lavfi.signalstats.YAVG=0.565225
        [Parsed_metadata_2 @ 0x1] frame:52   pts:2652    pts_time:0.887
        [Parsed_metadata_2 @ 0x1] lavfi.signalstats.YAVG=0.0438933
        [Parsed_metadata_2 @ 0x1] frame:53   pts:2706    pts_time:0.902
        [Parsed_metadata_2 @ 0x1] lavfi.signalstats.YAVG=6.19078
        [Parsed_metadata_2 @ 0x1] frame:54   pts:2760    pts_time:0.92
        [Parsed_metadata_2 @ 0x1] lavfi.signalstats.YAVG=5.44916
        """;

    [Fact]
    public void LeadIn_IsFirstFrameWithRealMotion() =>
        Assert.Equal(0.902, ClipFinisher.ParseLeadIn(PausedThenPlaying), 3);

    [Fact]
    public void LeadIn_IgnoresFirstFrameDifference()
    {
        const string log = """
            [x] frame:0    pts:0       pts_time:0.01
            [x] lavfi.signalstats.YAVG=40.0
            [x] frame:1    pts:60      pts_time:0.5
            [x] lavfi.signalstats.YAVG=7.5
            """;
        Assert.Equal(0.5, ClipFinisher.ParseLeadIn(log), 3);
    }

    [Fact]
    public void LeadIn_NoMotion() => Assert.Equal(0, ClipFinisher.ParseLeadIn("nothing here"));

    // Real Steam console_log lines from a launch stuck on the shader pre-caching prompt.
    private const string SteamStuckOnShaderPrompt = """
        [2026-11-11 14:31:35] GameAction [AppID 1422450, ActionID 35] : LaunchApp changed task to CheckShaderDepotManifest with ""
        [2026-11-11 14:31:40] GameAction [AppID 1422450, ActionID 35] : LaunchApp changed task to ProcessingShaderCache with ""
        [2026-11-11 14:31:40] GameAction [AppID 1422450, ActionID 35] : LaunchApp waiting for user response to ProcessingShaderCache ""
        [2026-11-11 14:31:40] GameAction [AppID 1422450, ActionID 35] : LaunchApp waiting for user response to ShowInterstitials ""
        """;

    [Fact]
    public void SteamPrompt_DetectsShaderCacheDialog() =>
        Assert.Equal("ProcessingShaderCache", GameSession.ParseSteamWaitingFor(SteamStuckOnShaderPrompt, 1422450));

    [Fact]
    public void SteamPrompt_ClearedWhenLaunchMovesOn() =>
        Assert.Null(GameSession.ParseSteamWaitingFor(SteamStuckOnShaderPrompt +
            "\n[2026-11-11 14:32:00] GameAction [AppID 1422450, ActionID 35] : LaunchApp changed task to Completed with \"\"", 1422450));

    [Fact]
    public void SteamPrompt_IgnoresOtherApps() =>
        Assert.Null(GameSession.ParseSteamWaitingFor(SteamStuckOnShaderPrompt.Replace("1422450", "730"), 1422450));

    [Fact]
    public void SinkInputs_FindsOnlyDeadlockStreams()
    {
        const string json = """
            [{"index":747,"properties":{"application.name":"Firefox"}},
             {"index":3487,"properties":{"application.name":"deadlock.exe"}}]
            """;
        Assert.Equal([3487], GameSession.ParseGameSinkInputs(json));
    }
}
