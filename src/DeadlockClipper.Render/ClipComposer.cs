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
