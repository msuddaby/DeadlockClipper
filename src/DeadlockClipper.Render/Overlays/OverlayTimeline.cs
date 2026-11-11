namespace DeadlockClipper.Render.Overlays;

public enum OverlayKind
{
    Intro,
    Kill,
    Final,
}

public enum OverlayAnimation
{
    SlideFromLeft,
    SlideFromRight,
    Fade,
}

/// <param name="Index">For kills: 0-based kill index within the clip.</param>
public sealed record OverlayCue(OverlayKind Kind, int Index, double Start, double End, OverlayAnimation Animation);

public sealed record OverlayTimelinePlan(IReadOnlyList<OverlayCue> Cues);

/// <summary>
/// When each overlay graphic is on screen (the chosen style: intro card from mockup A, kill slashes and the
/// multikill banner from mockup C). Pure, so timing can be unit-tested.
/// </summary>
public static class OverlayTimeline
{
    /// <summary>The game's own KILL pop lands ~0-0.2 s after the death event tick; markers follow it.</summary>
    public const double MarkerDelay = 0.1;

    public const double IntroStart = 0.2, IntroEnd = 3.0;
    public const double KillHold = 1.1, LastKillHold = 0.9;
    public const double FinalLag = 0.15, FinalHold = 2.45;

    public static OverlayTimelinePlan Plan(RenderJob job)
    {
        var cues = new List<OverlayCue>();
        var duration = job.DurationSeconds;

        // The intro shares the screen with nothing; drop it early if a kill lands during it.
        var firstKill = job.KillMarks.Count > 0 ? job.KillMarks.Min(k => k.OffsetSeconds) + MarkerDelay : double.MaxValue;
        var introEnd = Math.Min(IntroEnd, firstKill - 0.05);
        if (introEnd - IntroStart >= 1.0)
            cues.Add(new OverlayCue(OverlayKind.Intro, 0, IntroStart, introEnd, OverlayAnimation.SlideFromLeft));

        var kills = job.KillMarks.OrderBy(k => k.OffsetSeconds).ToList();
        for (var i = 0; i < kills.Count; i++)
        {
            if (!kills[i].ShowMarker) continue;
            var start = kills[i].OffsetSeconds + MarkerDelay;
            if (start >= duration) continue;
            var end = start + (i == kills.Count - 1 ? LastKillHold : KillHold);
            // Kills close together: each slash makes way for the next.
            if (i + 1 < kills.Count) end = Math.Min(end, kills[i + 1].OffsetSeconds + MarkerDelay - 0.02);
            end = Math.Min(end, duration);
            if (end - start < 0.15) continue;
            cues.Add(new OverlayCue(OverlayKind.Kill, i, start, end, OverlayAnimation.SlideFromLeft));
        }

        if (kills.Count >= 2)
        {
            var start = kills[^1].OffsetSeconds + MarkerDelay + FinalLag;
            var end = Math.Min(start + FinalHold, duration);
            if (end - start >= 0.5) cues.Add(new OverlayCue(OverlayKind.Final, 0, start, end, OverlayAnimation.SlideFromRight));
        }

        return new OverlayTimelinePlan(cues);
    }
}
