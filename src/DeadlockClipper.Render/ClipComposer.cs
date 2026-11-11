using System.Globalization;
using System.Text;

namespace DeadlockClipper.Render;

/// <summary>A text file the filter graph reads caption text from (avoids ffmpeg escaping of player names).</summary>
public sealed record CaptionText(string FileName, string Text);

public sealed record ComposePlan(string FilterGraph, IReadOnlyList<CaptionText> Texts);

/// <summary>Builds the ffmpeg filter graph that turns a raw recording into the finished clip.</summary>
public static class ClipComposer
{
    private const double IntroStart = 0.2;
    private const double IntroEnd = 2.8;
    private const double KillHold = 1.6;   // how long a "KILL" stays up
    private const double FinalHold = 2.5;  // how long the multikill label stays up

    /// <param name="textDir">Directory the caption text files will be written to (paths are embedded in the graph).</param>
    public static ComposePlan Build(RenderJob job, int fps, int height, string textDir)
    {
        var texts = new List<CaptionText>();
        var filters = new List<string> { $"fps={fps}" };
        var scale = height / 1080.0;
        int Px(double v) => (int)Math.Round(v * scale);

        string Text(string name, string value)
        {
            texts.Add(new CaptionText(name, value));
            return Path.Combine(textDir, name);
        }

        filters.Add(DrawText(Text("intro_title.txt", job.IntroTitle), "Sans Bold", Px(44), "white", Px(3), Px(150), IntroStart, IntroEnd));
        if (!string.IsNullOrWhiteSpace(job.IntroSubtitle))
            filters.Add(DrawText(Text("intro_sub.txt", job.IntroSubtitle), "Sans", Px(26), "white@0.9", Px(2), Px(205), IntroStart, IntroEnd));

        var duration = job.DurationSeconds;
        for (var i = 0; i < job.KillMarks.Count; i++)
        {
            var mark = job.KillMarks[i];
            if (!mark.ShowMarker && !mark.IsFinal) continue;
            var end = Math.Min(mark.OffsetSeconds + (mark.IsFinal ? FinalHold : KillHold), duration);
            // A "KILL" would overlap the next caption; cut it short when kills are close together.
            if (!mark.IsFinal && i + 1 < job.KillMarks.Count)
                end = Math.Min(end, job.KillMarks[i + 1].OffsetSeconds - 0.05);
            if (end <= mark.OffsetSeconds) continue;

            filters.Add(mark.IsFinal
                ? DrawText(Text($"kill_{i}.txt", mark.Label), "Sans Bold", Px(84), "0xFFD24A", Px(5), Px(150), mark.OffsetSeconds, end)
                : DrawText(Text($"kill_{i}.txt", mark.Label), "Sans Bold", Px(52), "white", Px(3), Px(170), mark.OffsetSeconds, end));
        }

        return new ComposePlan(string.Join(',', filters), texts);
    }

    /// <summary>
    /// Filter graph for the styled overlays. <paramref name="input"/> is the video stream; the overlay PNGs are inputs
    /// <paramref name="firstOverlayInput"/>.. in layer order, each looped for <see cref="OverlayInputSeconds"/> (only
    /// while it's on screen) and shifted to its cue start here. Output label: [vout].
    /// </summary>
    /// <remarks>
    /// Looping each PNG for the whole clip made ffmpeg process every graphic on every frame: a 13-kill compilation
    /// pushed ~77k full 1080p RGBA frames through fades for graphics that are each visible for 1-3 s.
    /// </remarks>
    public static string BuildOverlayGraph(Overlays.OverlayRender overlays, int fps, int width, int height, int firstOverlayInput,
        string input = "0:v")
    {
        var chains = new List<string> { $"[{input}]fps={fps},setpts=PTS-STARTPTS[v0]" };
        var last = "v0";
        var scale = width != 1920 || height != 1080 ? $",scale={width}:{height}" : "";

        for (var i = 0; i < overlays.Layers.Count; i++)
        {
            var cue = overlays.Layers[i].Cue;
            var s = F(cue.Start);
            var e = F(cue.End);
            var length = OverlayInputSeconds(cue);
            var fadeIn = cue.Animation == Overlays.OverlayAnimation.Fade ? 0.25 : 0.12;
            // Fades are relative to the graphic's own short stream, which is then moved to start at the cue.
            chains.Add($"[{firstOverlayInput + i}:v]format=rgba{scale},fade=in:st=0:d={F(fadeIn)}:alpha=1," +
                       $"fade=out:st={F(Math.Max(0, length - 0.3))}:d=0.3:alpha=1,setpts=PTS-STARTPTS+{s}/TB[o{i}]");

            // Ease-out cubic over 0.28 s from the cue start.
            var p = $"(1-pow(1-min(max((t-{s})/0.28\\,0)\\,1)\\,3))";
            var slide = (int)Math.Round(260 * width / 1920.0);
            var x = cue.Animation switch
            {
                Overlays.OverlayAnimation.SlideFromLeft => $"-{slide}*(1-{p})",
                Overlays.OverlayAnimation.SlideFromRight => $"{slide}*(1-{p})",
                _ => "0",
            };
            // eof_action=pass: once a graphic's stream ends, frames pass through untouched.
            chains.Add($"[{last}][o{i}]overlay=x='{x}':y=0:eof_action=pass:enable='between(t\\,{s}\\,{e})'[v{i + 1}]");
            last = $"v{i + 1}";
        }

        chains.Add($"[{last}]null[vout]");
        return string.Join(';', chains);
    }

    /// <summary>How long an overlay graphic's input stream needs to be: just its time on screen.</summary>
    public static double OverlayInputSeconds(Overlays.OverlayCue cue) => Math.Max(0.1, cue.End - cue.Start);

    /// <summary>Fade in over 0.2 s, hold, fade out over 0.4 s.</summary>
    public static string FadeAlpha(double start, double end)
    {
        var s = F(start);
        var e = F(end);
        var fadeIn = F(start + 0.2);
        var fadeOutStart = F(Math.Max(start + 0.2, end - 0.4));
        return $"if(lt(t\\,{s})\\,0\\,if(lt(t\\,{fadeIn})\\,(t-{s})/0.2\\,if(lt(t\\,{fadeOutStart})\\,1\\,if(lt(t\\,{e})\\,({e}-t)/0.4\\,0))))";
    }

    private static string DrawText(string textFile, string font, int size, string color, int border, int y, double start, double end)
    {
        var sb = new StringBuilder("drawtext=");
        sb.Append("textfile=").Append(EscapeValue(textFile));
        sb.Append(":expansion=none"); // caption text is literal; '%' in a player name must not be a template

        sb.Append(":font=").Append(EscapeValue(font));
        sb.Append(":fontsize=").Append(size);
        sb.Append(":fontcolor=").Append(color);
        sb.Append(":borderw=").Append(border);
        sb.Append(":bordercolor=black@0.85");
        sb.Append(":x=(w-tw)/2");
        sb.Append(":y=").Append(y);
        sb.Append(":alpha=").Append(FadeAlpha(start, end));
        return sb.ToString();
    }

    // Filtergraph option values: escape the characters that end an option or filter.
    private static string EscapeValue(string value) =>
        value.Replace("\\", "\\\\").Replace(":", "\\:").Replace(",", "\\,").Replace("'", "\\'").Replace(" ", "\\ ");

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
