using DeadlockClipper.Core.Parsing;

namespace DeadlockClipper.Core.Highlights;

public sealed record DetectionContext(
    double TickRate,
    int TotalTicks,
    IReadOnlyList<PlayerInfo> Players,
    IReadOnlyList<KillEvent> Kills);

public interface IHighlightDetector
{
    IEnumerable<Highlight> Detect(DetectionContext context);
}
