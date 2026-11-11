namespace DeadlockClipper.Render;

public sealed record SeekPlan(bool RequiresReload, IReadOnlyList<int> Hops);

/// <summary>
/// Deadlock crashes a few seconds after a single long demo_gototick jump (null deref in client.dll), but
/// survives the same distance in shorter hops. Seeks only ever go forward; a target behind the current
/// position means the demo must be reloaded and the seek planned from the start.
/// </summary>
public static class SeekPlanner
{
    /// <summary>
    /// Where playback should start for a segment so the game has <paramref name="preRollTicks"/> of playing time to
    /// settle before the part that's kept. Right after a jump the game stutters while it catches up, and that only
    /// happens once it plays, not while paused. If the demo is already positioned shortly before the segment (the
    /// previous segment just ended there), it plays through the gap instead of jumping again.
    /// </summary>
    public static int PlayFrom(int currentTick, bool demoLoaded, int segmentStart, int preRollTicks)
    {
        if (demoLoaded && currentTick <= segmentStart && segmentStart - currentTick <= preRollTicks) return currentTick;
        return Math.Max(0, segmentStart - preRollTicks);
    }

    public static SeekPlan Plan(int currentTick, int targetTick, int maxStep)
    {
        if (maxStep <= 0) throw new ArgumentOutOfRangeException(nameof(maxStep));

        var reload = targetTick < currentTick;
        var from = reload ? 0 : currentTick;

        var hops = new List<int>();
        for (var tick = from; tick < targetTick;)
        {
            tick = Math.Min(tick + maxStep, targetTick);
            hops.Add(tick);
        }
        return new SeekPlan(reload, hops);
    }
}
