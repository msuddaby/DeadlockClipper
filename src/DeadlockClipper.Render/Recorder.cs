using System.Globalization;

namespace DeadlockClipper.Render;

/// <param name="PreRollSeconds">Warm-up playback at the start (after the paused lead-in) that's cut from the clip.</param>
public sealed record RawRecording(string VideoPath, string AudioPath, double PreRollSeconds = 0);

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
    /// <param name="raws">One recording per segment of the job, in order.</param>
    Task FinishAsync(RenderJob job, IReadOnlyList<RawRecording> raws, string workDir, CancellationToken ct);
}

/// <summary>
/// Trims each recording's paused lead-in, joins the segments (with a quick dip to black between them), muxes audio,
/// forces constant frame rate and adds captions: the styled overlays (hero art, animated kill markers) when they can
/// be rendered, otherwise plain text.
/// </summary>
public sealed class ClipFinisher(RenderOptions options, IProcessRunner runner, Overlays.IOverlayArtist? artist = null) : IClipFinisher
{
    public async Task FinishAsync(RenderJob job, IReadOnlyList<RawRecording> raws, string workDir, CancellationToken ct)
    {
        var segments = job.ClipSegments;
        if (raws.Count != segments.Count)
            throw new ArgumentException($"Expected {segments.Count} recordings, got {raws.Count}.", nameof(raws));

        var inputs = new List<SegmentInput>();
        for (var i = 0; i < raws.Count; i++)
        {
            var hasAudio = File.Exists(raws[i].AudioPath) && new FileInfo(raws[i].AudioPath).Length > 1024;
            // Skip the paused frames, then the warm-up playback.
            var lead = await DetectLeadInAsync(raws[i].VideoPath, ct) + raws[i].PreRollSeconds;
            inputs.Add(new SegmentInput(lead, job.SegmentSeconds(segments[i]), hasAudio));
        }
        var overlays = artist == null ? null : await artist.RenderAsync(job, workDir, ct);

        var duration = F(job.DurationSeconds);
        var args = new List<string> { "-v", "error", "-y" };
        for (var i = 0; i < raws.Count; i++)
        {
            // Read a little past the segment so the trim in the graph, not EOF, decides where it ends.
            var lead = F(inputs[i].LeadIn);
            var read = F(inputs[i].Seconds + 1);
            args.AddRange(["-ss", lead, "-t", read, "-i", raws[i].VideoPath]);
            if (inputs[i].HasAudio) args.AddRange(["-ss", lead, "-t", read, "-i", raws[i].AudioPath]);
        }
        var nextInput = raws.Count + inputs.Count(s => s.HasAudio);
        var graph = new List<string> { JoinGraph(inputs, options.Fps) };

        if (overlays is { Layers.Count: > 0 })
        {
            foreach (var layer in overlays.Layers)
                args.AddRange(["-loop", "1", "-framerate", options.Fps.ToString(), "-t", F(ClipComposer.OverlayInputSeconds(layer.Cue)), "-i", layer.PngPath]);
            graph.Add(ClipComposer.BuildOverlayGraph(overlays, options.Fps, options.Width, options.Height, nextInput, input: "joined"));
        }
        else
        {
            var textDir = Path.Combine(workDir, "captions");
            Directory.CreateDirectory(textDir);
            var plan = ClipComposer.Build(job, options.Fps, options.Height, textDir);
            foreach (var t in plan.Texts) await File.WriteAllTextAsync(Path.Combine(textDir, t.FileName), t.Text, ct);
            graph.Add($"[joined]{plan.FilterGraph}[vout]");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(job.OutputPath)!);
        var tmp = job.OutputPath + ".part.mp4";
        args.AddRange([
            "-filter_complex", string.Join(';', graph),
            "-map", "[vout]", "-map", "[aout]", "-t", duration,
            "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "160k",
            "-movflags", "+faststart",
            tmp,
        ]);
        // Encoding runs at roughly real time or better; long compilations need more than the old fixed 5 minutes.
        var timeout = TimeSpan.FromSeconds(Math.Max(300, job.DurationSeconds * 6));
        var r = await runner.RunAsync("ffmpeg", args, timeout: timeout, ct: ct);
        if (!r.Success) throw new InvalidOperationException($"ffmpeg failed: {r.StdErr.Trim()}");
        File.Move(tmp, job.OutputPath, overwrite: true);
    }

    internal sealed record SegmentInput(double LeadIn, double Seconds, bool HasAudio);

    /// <summary>Gap-free fade between segments, in seconds (each side).</summary>
    internal const double SegmentFade = 0.15;

    /// <summary>
    /// Filter graph joining the segment inputs (video, then audio if present, per segment, in input order) into
    /// [joined] and [aout]. Each piece is padded/trimmed to exactly its planned length so captions stay in sync.
    /// </summary>
    internal static string JoinGraph(IReadOnlyList<SegmentInput> segments, int fps)
    {
        var chains = new List<string>();
        var concat = new System.Text.StringBuilder();
        var index = 0;
        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            var d = F(s.Seconds);
            var fadeIn = i > 0 ? $",fade=in:st=0:d={F(SegmentFade)}" : "";
            var fadeOut = i < segments.Count - 1 ? $",fade=out:st={F(Math.Max(0, s.Seconds - SegmentFade))}:d={F(SegmentFade)}" : "";
            var afadeIn = i > 0 ? $",afade=in:st=0:d={F(SegmentFade)}" : "";
            var afadeOut = i < segments.Count - 1 ? $",afade=out:st={F(Math.Max(0, s.Seconds - SegmentFade))}:d={F(SegmentFade)}" : "";

            chains.Add($"[{index++}:v]fps={fps},setpts=PTS-STARTPTS,tpad=stop_mode=clone:stop_duration=2,trim=duration={d},setpts=PTS-STARTPTS{fadeIn}{fadeOut}[sv{i}]");
            chains.Add(s.HasAudio
                ? $"[{index++}:a]aformat=sample_rates=48000:channel_layouts=stereo,asetpts=PTS-STARTPTS,apad,atrim=duration={d},asetpts=PTS-STARTPTS{afadeIn}{afadeOut}[sa{i}]"
                : $"anullsrc=r=48000:cl=stereo,atrim=duration={d}[sa{i}]");
            concat.Append($"[sv{i}][sa{i}]");
        }
        chains.Add($"{concat}concat=n={segments.Count}:v=1:a=1[joined][aout]");
        return string.Join(';', chains);
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

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
