using DeadlockClipper.Render;

namespace DeadlockClipper.Render.Tests;

public class SteamLaunchTests
{
    // Steam's console_log.txt when a launch hit a game update (2026-11-11 16:22).
    private const string UpdateAbort = """
        [2026-11-11 16:22:04] ExecuteSteamURL: "steam://rungameid/1422450"
        [2026-11-11 16:22:04] GameAction [AppID 1422450, ActionID 38] : LaunchApp changed task to UpdatingAppInfo with ""
        [2026-11-11 16:22:04] GameAction [AppID 1422450, ActionID 38] : LaunchApp changed task to CheckShaderDepotManifest with ""
        [2026-11-11 16:22:06] GameAction [AppID 1422450, ActionID 38] : LaunchApp changed task to DownloadingDepots with ""
        [2026-11-11 16:22:11] GameAction [AppID 1422450, ActionID 38] : LaunchApp failed with AppError_19 with ""
        [2026-11-11 16:22:11] GameAction [AppID 1422450, ActionID 38] : LaunchApp changed task to Failed with ""
        """;

    [Fact]
    public void UpdateThatAbortsTheLaunch_IsRecognised()
    {
        var s = GameSession.ParseSteamLaunch(UpdateAbort, 1422450);
        Assert.True(s.Failed);
        Assert.True(s.Updating);
        Assert.Equal("AppError_19", s.Error);
    }

    [Fact]
    public void UpdateInProgress_IsNotYetAFailure()
    {
        var s = GameSession.ParseSteamLaunch(string.Join('\n', UpdateAbort.Split('\n').Take(4)), 1422450);
        Assert.False(s.Failed);
        Assert.True(s.Updating);
    }

    [Fact]
    public void OtherAppsAndNormalLaunches_AreIgnored()
    {
        var other = UpdateAbort.Replace("AppID 1422450", "AppID 570");
        Assert.Equal(new GameSession.SteamLaunchState(false, null, false), GameSession.ParseSteamLaunch(other, 1422450));
    }

    [Theory]
    [InlineData("\"StateFlags\"\t\t\"4\"", "4")]
    [InlineData("\"AppState\"\n{\n\t\"StateFlags\"\t\t\"6\"\n\t\"buildid\"\t\t\"1\"", "6")]
    [InlineData("\"buildid\"\t\t\"1\"", null)]
    public void StateFlags(string acf, string? expected) => Assert.Equal(expected, GameSession.ParseStateFlags(acf));
}
