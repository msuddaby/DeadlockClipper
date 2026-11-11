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
    /// Filter graph for the styled overlays. Input 0 is the video; the overlay PNGs are inputs
    /// <paramref name="firstOverlayInput"/>.. in layer order, each looped for the clip's length. Output label: [vout].
    /// </summary>
    public static string BuildOverlayGraph(Overlays.OverlayRender overlays, int fps, int width, int height, int firstOverlayInput)
    {
        var chains = new List<string> { $"[0:v]fps={fps},setpts=PTS-STARTPTS[v0]" };
        var last = "v0";
        var scale = width != 1920 || height != 1080 ? $",scale={width}:{height}" : "";

        for (var i = 0; i < overlays.Layers.Count; i++)
        {
            var cue = overlays.Layers[i].Cue;
            var s = F(cue.Start);
            var e = F(cue.End);
            var fadeIn = cue.Animation == Overlays.OverlayAnimation.Fade ? 0.25 : 0.12;
            chains.Add($"[{firstOverlayInput + i}:v]format=rgba{scale},fade=in:st={s}:d={F(fadeIn)}:alpha=1," +
                       $"fade=out:st={F(Math.Max(cue.Start, cue.End - 0.3))}:d=0.3:alpha=1[o{i}]");

            // Ease-out cubic over 0.28 s from the cue start.
            var p = $"(1-pow(1-min(max((t-{s})/0.28\\,0)\\,1)\\,3))";
            var slide = (int)Math.Round(260 * width / 1920.0);
            var x = cue.Animation switch
            {
                Overlays.OverlayAnimation.SlideFromLeft => $"-{slide}*(1-{p})",
                Overlays.OverlayAnimation.SlideFromRight => $"{slide}*(1-{p})",
                _ => "0",
            };
            chains.Add($"[{last}][o{i}]overlay=x='{x}':y=0:enable='between(t\\,{s}\\,{e})'[v{i + 1}]");
            last = $"v{i + 1}";
        }

        for (var j = 0; j < overlays.Flashes.Count; j++)
        {
            var t = overlays.Flashes[j];
            chains.Add($"[{last}]drawbox=x=0:y=0:w=iw:h=ih:color=white@0.4:t=fill:" +
                       $"enable='between(t\\,{F(t)}\\,{F(t + Overlays.OverlayTimeline.FlashLength)})'[f{j}]");
            last = $"f{j}";
        }

        chains.Add($"[{last}]null[vout]");
        return string.Join(';', chains);
    }

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
