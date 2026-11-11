using DeadlockClipper.Render;

namespace DeadlockClipper.Render.Tests;

public class SeekPlannerTests
{
    [Fact]
    public void LongForwardSeek_IsSplitIntoHops()
    {
        var plan = SeekPlanner.Plan(637, 98431, 20000);
        Assert.False(plan.RequiresReload);
        Assert.Equal([20637, 40637, 60637, 80637, 98431], plan.Hops);
    }

    [Fact]
    public void ShortForwardSeek_IsOneHop()
    {
        var plan = SeekPlanner.Plan(90000, 98431, 20000);
        Assert.Equal([98431], plan.Hops);
    }

    [Fact]
    public void AlreadyThere_NoHops()
    {
        Assert.Empty(SeekPlanner.Plan(98431, 98431, 20000).Hops);
    }

    [Fact]
    public void BackwardTarget_RequiresReloadAndPlansFromStart()
    {
        var plan = SeekPlanner.Plan(120000, 30000, 20000);
        Assert.True(plan.RequiresReload);
        Assert.Equal([20000, 30000], plan.Hops);
    }
}
