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

/// <summary>Trims the paused lead-in, muxes audio, forces constant frame rate and burns in captions.</summary>
public sealed class ClipFinisher(RenderOptions options, IProcessRunner runner) : IClipFinisher
{
    public async Task FinishAsync(RenderJob job, RawRecording raw, string workDir, CancellationToken ct)
    {
        var lead = await DetectLeadInAsync(raw.VideoPath, ct);

        var textDir = Path.Combine(workDir, "captions");
        Directory.CreateDirectory(textDir);
        var plan = ClipComposer.Build(job, options.Fps, options.Height, textDir);
        foreach (var t in plan.Texts) await File.WriteAllTextAsync(Path.Combine(textDir, t.FileName), t.Text, ct);

        Directory.CreateDirectory(Path.GetDirectoryName(job.OutputPath)!);
        var tmp = job.OutputPath + ".part.mp4";
        var leadArg = lead.ToString("0.###", CultureInfo.InvariantCulture);
        var r = await runner.RunAsync("ffmpeg",
        [
            "-v", "error", "-y",
            "-ss", leadArg, "-i", raw.VideoPath,
            "-ss", leadArg, "-i", raw.AudioPath,
            "-map", "0:v", "-map", "1:a?",
            "-t", job.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-vf", plan.FilterGraph,
            "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "160k",
            "-movflags", "+faststart",
            tmp,
        ], timeout: TimeSpan.FromMinutes(5), ct: ct);
        if (!r.Success) throw new InvalidOperationException($"ffmpeg failed: {r.StdErr.Trim()}");
        File.Move(tmp, job.OutputPath, overwrite: true);
    }

    /// <summary>The recorder starts before demo_resume, so the clip opens on frozen (paused) frames.</summary>
    private async Task<double> DetectLeadInAsync(string video, CancellationToken ct)
    {
        var r = await runner.RunAsync("ffmpeg",
            ["-hide_banner", "-t", "5", "-i", video, "-vf", "freezedetect=n=0.003:d=0.1", "-map", "0:v", "-f", "null", "-"],
            timeout: TimeSpan.FromMinutes(1), ct: ct);
        return ParseLeadIn(r.StdErr);
    }

    internal static double ParseLeadIn(string ffmpegLog)
    {
        double? start = null;
        foreach (var line in ffmpegLog.Split('\n'))
        {
            var i = line.IndexOf("freeze_start: ", StringComparison.Ordinal);
            if (i >= 0 && start == null)
            {
                start = double.Parse(line[(i + 14)..].Trim(), CultureInfo.InvariantCulture);
                continue;
            }
            i = line.IndexOf("freeze_end: ", StringComparison.Ordinal);
            if (i >= 0 && start != null)
                // Only a freeze at the very start is the paused lead-in.
                return start < 0.2 ? double.Parse(line[(i + 12)..].Trim(), CultureInfo.InvariantCulture) : 0;
        }
        return 0;
    }
}
