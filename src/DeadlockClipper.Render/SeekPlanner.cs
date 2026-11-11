namespace DeadlockClipper.Render;

public sealed record SeekPlan(bool RequiresReload, IReadOnlyList<int> Hops);

/// <summary>
/// Deadlock crashes a few seconds after a single long demo_gototick jump (null deref in client.dll), but
/// survives the same distance in shorter hops. Seeks only ever go forward; a target behind the current
/// position means the demo must be reloaded and the seek planned from the start.
/// </summary>
public static class SeekPlanner
{
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
