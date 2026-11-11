using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DeadlockClipper.Render;

public sealed class GameCrashedException(string message) : Exception(message);

/// <summary>Deadlock is running normally (the player's own session), so it can't be used for rendering.</summary>
public sealed class GameInUseException() : Exception("Deadlock is already running. Close it before rendering clips.");

/// <summary>Drives one render-mode Deadlock instance. Abstracted so the renderer can be tested without the game.</summary>
public interface IGameSession
{
    bool IsLaunched { get; }
    /// <summary>Path passed to the last successful playdemo, or null if no demo is loaded.</summary>
    string? LoadedDemo { get; set; }
    /// <summary>Best-known current demo tick (only ever moved forward by seeks/playback).</summary>
    int CurrentTick { get; set; }

    Task LaunchAsync(CancellationToken ct);
    Task SendAsync(string commands, CancellationToken ct);
    /// <summary>Remembers the current end of console.log so the next wait only sees new lines.</summary>
    void MarkLog();
    Task<bool> WaitForLogAsync(string text, TimeSpan timeout, CancellationToken ct);
    Task<bool> IsAliveAsync(CancellationToken ct);
    Task QuitAsync(CancellationToken ct);
}

/// <summary>
/// Linux implementation: the game is launched through Steam with a render request that
/// tools/deadlock-launch-wrapper.sh turns into a headless gamescope session. Commands are written to a cfg
/// file and executed by pressing a bound key on gamescope's private X display (no focus needed).
/// </summary>
public sealed class GameSession(RenderOptions options, IProcessRunner runner, ILogger<GameSession> log) : IGameSession
{
    private const string CommandCfg = "clipper_cmd";
    private const string BootCfg = "clipper_boot";

    private string? _display;
    private string? _window;
    private long _logOffset;

    public bool IsLaunched => _display != null;
    public string? LoadedDemo { get; set; }
    public int CurrentTick { get; set; }

    private string LogPath => Path.Combine(options.GameDir, "console.log");
    private static string RuntimeDir =>
        Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "/tmp", "deadlock-clipper");

    public async Task LaunchAsync(CancellationToken ct)
    {
        Reset();
        if (await IsAliveAsync(ct))
        {
            if (await FindGamescopeDisplayAsync(ct) == null) throw new GameInUseException();
            // A render-mode game left over from an earlier session; replace it with a fresh one.
            log.LogInformation("Stopping leftover render-mode game");
            await KillGameAsync(ct);
        }

        // The boot cfg binds the key used to run commands; the wrapper execs it in render mode.
        var cfgDir = Path.Combine(options.GameDir, "cfg");
        await File.WriteAllTextAsync(Path.Combine(cfgDir, BootCfg + ".cfg"), $"bind PAUSE \"exec {CommandCfg}\"\n", ct);
        await File.WriteAllTextAsync(Path.Combine(cfgDir, CommandCfg + ".cfg"), "", ct);

        Directory.CreateDirectory(RuntimeDir);
        await File.WriteAllTextAsync(Path.Combine(RuntimeDir, "render-request"),
            $"WIDTH={options.Width}\nHEIGHT={options.Height}\nFPS={options.Fps}\n", ct);

        log.LogInformation("Launching Deadlock in render mode");
        await runner.RunAsync("steam", [$"steam://rungameid/{options.SteamAppId}"], timeout: TimeSpan.FromSeconds(10), ct: ct);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(options.LaunchTimeoutSeconds);
        while (_window == null)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Deadlock did not start in render mode.");
            await Task.Delay(1000, ct);
            _display ??= await FindGamescopeDisplayAsync(ct);
            if (_display != null) _window = await FindWindowAsync(ct);
        }
        log.LogInformation("Game window {Window} on gamescope display {Display}", _window, _display);

        // console.log is recreated by -condebug at startup; wait for the hideout (main menu) to load.
        _logOffset = 0;
        if (!await WaitForLogAsync("Host activate: Loading (dl_hideout)", TimeSpan.FromSeconds(options.LaunchTimeoutSeconds), ct))
            throw new TimeoutException("Deadlock did not reach the main menu.");
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        await RouteAudioToSilentSinkAsync(ct);

        // The first key press after launch only dismisses the title screen.
        await PressAsync("space", ct);
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
    }

    /// <summary>
    /// Writes the commands to the cfg file, presses the bound key, and waits until the game logs that it
    /// executed the file. The game reads the file a frame or more after the key press, so without this wait a
    /// following SendAsync can overwrite the file first and the earlier commands are silently lost.
    /// </summary>
    public async Task SendAsync(string commands, CancellationToken ct)
    {
        if (_window == null) throw new InvalidOperationException("Game not launched.");
        await File.WriteAllTextAsync(Path.Combine(options.GameDir, "cfg", CommandCfg + ".cfg"), commands.TrimEnd() + "\n", ct);

        const string executed = $"execing {CommandCfg}";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var before = LogLength();
            await PressAsync("Pause", ct);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50, ct);
                if (ReadLog(before).Contains(executed, StringComparison.Ordinal)) return;
            }
            if (!await IsAliveAsync(ct)) throw new GameCrashedException("Game exited while sending commands.");
            log.LogWarning("Game did not run commands within 5s; pressing again");
        }
        throw new TimeoutException($"Game did not execute commands: {commands}");
    }

    public void MarkLog() => _logOffset = LogLength();

    private long LogLength() => File.Exists(LogPath) ? new FileInfo(LogPath).Length : 0;

    public async Task<bool> WaitForLogAsync(string text, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (ReadLog(_logOffset).Contains(text, StringComparison.Ordinal)) return true;
            if (!await IsAliveAsync(ct)) throw new GameCrashedException($"Game exited while waiting for '{text}'.");
            await Task.Delay(500, ct);
        }
        return false;
    }

    /// <summary>
    /// Under Proton the game's process name is "MainThrd"; its command line is the Windows path
    /// ("S:\steamapps\…\deadlock.exe …", drive letter depends on the Steam library).
    /// </summary>
    public async Task<bool> IsAliveAsync(CancellationToken ct) =>
        (await runner.RunAsync("pgrep", ["-f", @"^[A-Za-z]:.*\\deadlock\.exe"], ct: ct)).Success;

    public async Task QuitAsync(CancellationToken ct)
    {
        if (_window != null && await IsAliveAsync(ct))
        {
            await SendAsync("quit", ct);
            for (var i = 0; i < 30 && await IsAliveAsync(ct); i++) await Task.Delay(1000, ct);
        }
        if (await IsAliveAsync(ct) && await FindGamescopeDisplayAsync(ct) != null) await KillGameAsync(ct);
        Reset();
    }

    private void Reset()
    {
        _display = null;
        _window = null;
        LoadedDemo = null;
        CurrentTick = 0;
    }

    private async Task KillGameAsync(CancellationToken ct)
    {
        // Killing gamescope ends the game; the wrapper then restores the player's settings.
        await runner.RunAsync("pkill", ["-TERM", "-f", "^gamescope --backend headless"], ct: ct);
        for (var i = 0; i < 20 && await IsAliveAsync(ct); i++) await Task.Delay(1000, ct);
    }

    private Task PressAsync(string key, CancellationToken ct) =>
        RunChecked("xdotool", ["key", "--window", _window!, key], ct, new Dictionary<string, string> { ["DISPLAY"] = _display! });

    private string ReadLog(long offset)
    {
        if (!File.Exists(LogPath)) return "";
        using var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < offset) offset = 0; // log was recreated
        fs.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>gamescope starts its own rootless Xwayland ("Xwayland :N -rootless -core -terminate …").</summary>
    private async Task<string?> FindGamescopeDisplayAsync(CancellationToken ct)
    {
        var r = await runner.RunAsync("pgrep", ["-a", "-f", "^Xwayland :[0-9]+ .*-terminate"], ct: ct);
        if (!r.Success) return null;
        var parts = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last().Split(' ');
        return parts.Length > 2 ? parts[2] : null;
    }

    private async Task<string?> FindWindowAsync(CancellationToken ct)
    {
        var r = await runner.RunAsync("xdotool", ["search", "--name", "^Deadlock$"],
            new Dictionary<string, string> { ["DISPLAY"] = _display! }, ct: ct);
        return r.Success ? r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() : null;
    }

    /// <summary>
    /// Wine ignores PULSE_SINK, so the game's stream is moved to the silent sink after it appears.
    /// The WirePlumber rule (tools/wireplumber) stops this move being remembered for normal play.
    /// </summary>
    private async Task RouteAudioToSilentSinkAsync(CancellationToken ct)
    {
        var sinks = await runner.RunAsync("pactl", ["list", "short", "sinks"], ct: ct);
        if (!sinks.StdOut.Contains(options.SilentSink))
            await RunChecked("pactl", ["load-module", "module-null-sink", $"sink_name={options.SilentSink}",
                "sink_properties=device.description=Deadlock-Clipper-Silent"], ct);

        for (var attempt = 0; attempt < 20; attempt++)
        {
            var inputs = await runner.RunAsync("pactl", ["-f", "json", "list", "sink-inputs"], ct: ct);
            var ids = ParseGameSinkInputs(inputs.StdOut);
            if (ids.Count > 0)
            {
                foreach (var id in ids) await RunChecked("pactl", ["move-sink-input", id.ToString(), options.SilentSink], ct);
                log.LogInformation("Routed game audio to {Sink}", options.SilentSink);
                return;
            }
            await Task.Delay(500, ct);
        }
        throw new InvalidOperationException("Could not find Deadlock's audio stream to silence it.");
    }

    internal static List<int> ParseGameSinkInputs(string json)
    {
        var ids = new List<int>();
        if (string.IsNullOrWhiteSpace(json)) return ids;
        using var doc = JsonDocument.Parse(json);
        foreach (var input in doc.RootElement.EnumerateArray())
        {
            if (input.TryGetProperty("properties", out var props)
                && props.TryGetProperty("application.name", out var name)
                && name.GetString() == "deadlock.exe")
                ids.Add(input.GetProperty("index").GetInt32());
        }
        return ids;
    }

    private async Task RunChecked(string file, string[] args, CancellationToken ct, IReadOnlyDictionary<string, string>? env = null)
    {
        var r = await runner.RunAsync(file, args, env, TimeSpan.FromSeconds(15), ct);
        if (!r.Success) throw new InvalidOperationException($"{file} {string.Join(' ', args)} failed: {r.StdErr.Trim()}");
    }
}
