using System.Globalization;

namespace DeadlockClipper.Render;

public sealed record RawRecording(string VideoPath, string AudioPath);

public interface IRecorder
{
    Task<IActiveRecording> StartAsync(string workDir, CancellationToken ct);
}

public interface IActiveRecording : IAsyncDisposable
{
    Task<RawRecording> StopAsync();
}

/// <summary>
/// Records gamescope's PipeWire video output (GStreamer, hardware H.264) and the silent sink's monitor (pw-record).
/// Both are stopped with SIGINT so their files are finalized.
/// </summary>
public sealed class Recorder(RenderOptions options, IProcessRunner runner) : IRecorder
{
    public async Task<IActiveRecording> StartAsync(string workDir, CancellationToken ct)
    {
        Directory.CreateDirectory(workDir);
        var video = Path.Combine(workDir, "raw.video.mp4");
        var audio = Path.Combine(workDir, "raw.audio.wav");
        File.Delete(video);
        File.Delete(audio);

        var encoder = options.VideoEncoder == "x264enc"
            ? new[] { "x264enc", $"bitrate={options.VideoBitrateKbps}", "speed-preset=veryfast" }
            : new[] { options.VideoEncoder, $"bitrate={options.VideoBitrateKbps}" };

        var gst = runner.Start("gst-launch-1.0",
        [
            "-e", "-q",
            "pipewiresrc", "target-object=gamescope", "do-timestamp=true", "!",
            "queue", "!", "videoconvert", "!", "video/x-raw,format=NV12", "!",
            .. encoder, "!",
            "h264parse", "!", "mp4mux", "!", "filesink", $"location={video}",
        ]);
        var pw = runner.Start("pw-record",
            ["-P", "{ stream.capture.sink=true }", "--target", options.SilentSink, audio]);

        // Give both pipelines time to connect before the demo resumes; the lead-in is trimmed later.
        await Task.Delay(TimeSpan.FromSeconds(1), ct);
        if (gst.HasExited) throw new InvalidOperationException($"Video recorder failed: {gst.StdErrSoFar.Trim()}");
        if (pw.HasExited) throw new InvalidOperationException($"Audio recorder failed: {pw.StdErrSoFar.Trim()}");

        return new ActiveRecording(gst, pw, new RawRecording(video, audio));
    }

    private sealed class ActiveRecording(IRunningProcess gst, IRunningProcess pw, RawRecording files) : IActiveRecording
    {
        public async Task<RawRecording> StopAsync()
        {
            await Task.WhenAll(gst.InterruptAsync(TimeSpan.FromSeconds(10)), pw.InterruptAsync(TimeSpan.FromSeconds(5)));
            if (!File.Exists(files.VideoPath) || new FileInfo(files.VideoPath).Length == 0)
                throw new InvalidOperationException($"No video was recorded. {gst.StdErrSoFar.Trim()}");
            return files;
        }

        public async ValueTask DisposeAsync()
        {
            await gst.DisposeAsync();
            await pw.DisposeAsync();
        }
    }
}

public interface IClipFinisher
{
    Task FinishAsync(RenderJob job, RawRecording raw, string workDir, CancellationToken ct);
}

/// <summary>
/// Trims the paused lead-in, muxes audio, forces constant frame rate and adds captions: the styled overlays (hero
/// art, animated kill markers) when they can be rendered, otherwise plain text.
/// </summary>
public sealed class ClipFinisher(RenderOptions options, IProcessRunner runner, Overlays.IOverlayArtist? artist = null) : IClipFinisher
{
    public async Task FinishAsync(RenderJob job, RawRecording raw, string workDir, CancellationToken ct)
    {
        var lead = await DetectLeadInAsync(raw.VideoPath, ct);
        var overlays = artist == null ? null : await artist.RenderAsync(job, workDir, ct);

        var leadArg = lead.ToString("0.###", CultureInfo.InvariantCulture);
        var duration = job.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var args = new List<string>
        {
            "-v", "error", "-y",
            "-ss", leadArg, "-i", raw.VideoPath,
            "-ss", leadArg, "-i", raw.AudioPath,
        };

        if (overlays is { Layers.Count: > 0 })
        {
            foreach (var layer in overlays.Layers)
                args.AddRange(["-loop", "1", "-framerate", options.Fps.ToString(), "-t", duration, "-i", layer.PngPath]);
            args.AddRange(["-filter_complex", ClipComposer.BuildOverlayGraph(overlays, options.Fps, options.Width, options.Height, 2),
                "-map", "[vout]"]);
        }
        else
        {
            var textDir = Path.Combine(workDir, "captions");
            Directory.CreateDirectory(textDir);
            var plan = ClipComposer.Build(job, options.Fps, options.Height, textDir);
            foreach (var t in plan.Texts) await File.WriteAllTextAsync(Path.Combine(textDir, t.FileName), t.Text, ct);
            args.AddRange(["-vf", plan.FilterGraph, "-map", "0:v"]);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(job.OutputPath)!);
        var tmp = job.OutputPath + ".part.mp4";
        args.AddRange([
            "-map", "1:a?", "-t", duration,
            "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "160k",
            "-movflags", "+faststart",
            tmp,
        ]);
        var r = await runner.RunAsync("ffmpeg", args, timeout: TimeSpan.FromMinutes(5), ct: ct);
        if (!r.Success) throw new InvalidOperationException($"ffmpeg failed: {r.StdErr.Trim()}");
        File.Move(tmp, job.OutputPath, overwrite: true);
    }

    /// <summary>
    /// The recorder starts before demo_resume, so the clip opens on paused frames. Those aren't perfectly frozen:
    /// the animated "GAME IS PAUSED" box changes each frame slightly (mean luma difference ~0.05-0.5), which made
    /// ffmpeg's freezedetect report playback ~0.6 s early. Real playback is a jump in frame-to-frame difference
    /// (5+), so the clip starts at the first frame whose difference from the previous one is large.
    /// </summary>
    private async Task<double> DetectLeadInAsync(string video, CancellationToken ct)
    {
        var r = await runner.RunAsync("ffmpeg",
        [
            "-hide_banner", "-t", "6", "-i", video,
            "-vf", "tblend=all_mode=difference,signalstats,metadata=print:key=lavfi.signalstats.YAVG",
            "-map", "0:v", "-f", "null", "-",
        ], timeout: TimeSpan.FromMinutes(1), ct: ct);
        return ParseLeadIn(r.StdErr);
    }

    /// <summary>Mean luma difference above which a frame counts as real playback, not the paused screen.</summary>
    internal const double PlaybackMotionThreshold = 2.0;

    /// <summary>Parses ffmpeg's metadata=print output (pts_time lines followed by YAVG lines).</summary>
    internal static double ParseLeadIn(string ffmpegLog)
    {
        double? pts = null;
        foreach (var line in ffmpegLog.Split('\n'))
        {
            var i = line.IndexOf("pts_time:", StringComparison.Ordinal);
            if (i >= 0)
            {
                var end = line.IndexOfAny([' ', '\t', '\r'], i + 9);
                pts = double.Parse(end < 0 ? line[(i + 9)..] : line[(i + 9)..end], CultureInfo.InvariantCulture);
                continue;
            }
            i = line.IndexOf("lavfi.signalstats.YAVG=", StringComparison.Ordinal);
            // Skip the very first frames: the first difference is against nothing.
            if (i >= 0 && pts is > 0.05
                && double.Parse(line[(i + 23)..].Trim(), CultureInfo.InvariantCulture) > PlaybackMotionThreshold)
                return pts.Value;
        }
        return 0;
    }
}
