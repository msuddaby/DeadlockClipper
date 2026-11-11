using DeadlockClipper.Render;

namespace DeadlockClipper.Api;

/// <param name="Required">Optional checks only affect extras (e.g. styled captions) and don't block rendering.</param>
public sealed record HealthCheck(string Id, string Label, bool Ok, string? Fix, bool Required = true);

public sealed record RenderHealthReport(bool Ok, IReadOnlyList<HealthCheck> Checks);

/// <summary>Verifies the machine is set up for rendering, so the UI can show what to fix instead of failing jobs.</summary>
public sealed class RenderHealth(RenderOptions options, IProcessRunner runner)
{
    private const string WrapperName = "deadlock-launch-wrapper.sh";
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private RenderHealthReport? _cached;
    private DateTime _cachedAt;

    public async Task<RenderHealthReport> CheckAsync(CancellationToken ct)
    {
        if (_cached != null && DateTime.UtcNow - _cachedAt < TimeSpan.FromSeconds(30)) return _cached;

        var checks = new List<HealthCheck>();

        foreach (var (tool, package) in new[]
                 {
                     ("gamescope", "gamescope"), ("gst-launch-1.0", "gstreamer"), ("xdotool", "xdotool"),
                     ("pactl", "libpulse"), ("pw-record", "pipewire"), ("ffmpeg", "ffmpeg"), ("steam", "steam"),
                 })
        {
            checks.Add(new HealthCheck($"tool:{tool}", $"{tool} installed", OnPath(tool), $"Install it: sudo pacman -S {package}"));
        }

        var encoder = await runner.RunAsync("gst-inspect-1.0", [options.VideoEncoder], timeout: TimeSpan.FromSeconds(10), ct: ct);
        checks.Add(new HealthCheck("encoder", $"GStreamer encoder {options.VideoEncoder} available", encoder.Success,
            options.VideoEncoder == "vah264enc"
                ? "Install gst-plugin-va, or set Clipper:Render:VideoEncoder to x264enc (CPU encoding)."
                : $"Install the GStreamer plugin that provides {options.VideoEncoder}."));

        checks.Add(new HealthCheck("game-dir", "Deadlock game folder found",
            Directory.Exists(Path.Combine(options.GameDir, "cfg")),
            "Set Clipper:ReplaysDir (or Clipper:Render:GameDir) to your Deadlock game/citadel folder."));

        checks.Add(new HealthCheck("launch-options", "Deadlock launch options use the clipper wrapper", LaunchOptionsUseWrapper(),
            $"In Steam: Deadlock → Properties → Launch Options, set: {WrapperPath()} %command%"));

        checks.Add(new HealthCheck("wireplumber", "Audio rule installed (keeps normal play audible)",
            File.Exists(Path.Combine(Home, ".config/wireplumber/wireplumber.conf.d/51-deadlock-clipper.conf")),
            "Copy tools/wireplumber/51-deadlock-clipper.conf to ~/.config/wireplumber/wireplumber.conf.d/ and run: systemctl --user restart wireplumber"));

        checks.Add(new HealthCheck("chromium", "Chromium installed (styled captions with hero art)", OnPath(options.ChromiumPath),
            "Install it for styled captions: sudo pacman -S chromium. Without it, clips get plain text captions.", Required: false));

        _cached = new RenderHealthReport(checks.Where(c => c.Required).All(c => c.Ok), checks);
        _cachedAt = DateTime.UtcNow;
        return _cached;
    }

    private static bool OnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(d => d.Length > 0 && File.Exists(Path.Combine(d, exe)));

    /// <summary>Steam stores per-game launch options in userdata/&lt;account&gt;/config/localconfig.vdf.</summary>
    private static bool LaunchOptionsUseWrapper()
    {
        var userdata = Path.Combine(Home, ".local/share/Steam/userdata");
        if (!Directory.Exists(userdata)) return false;
        return Directory.EnumerateDirectories(userdata)
            .Select(d => Path.Combine(d, "config", "localconfig.vdf"))
            .Where(File.Exists)
            .Any(f => File.ReadLines(f).Any(l => l.Contains("\"LaunchOptions\"") && l.Contains(WrapperName)));
    }

    private static string WrapperPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tools", WrapperName);
            if (File.Exists(candidate)) return candidate;
        }
        return $"/path/to/DeadlockClipper/tools/{WrapperName}";
    }
}
