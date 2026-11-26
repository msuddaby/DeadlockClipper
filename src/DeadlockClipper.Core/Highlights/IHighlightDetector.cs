using DeadlockClipper.Core.Parsing;

namespace DeadlockClipper.Core.Highlights;

public sealed record DetectionContext(
    double TickRate,
    int TotalTicks,
    IReadOnlyList<PlayerInfo> Players,
    IReadOnlyList<KillEvent> Kills,
    // Null when the source has no chat (match metadata).
    IReadOnlyList<ChatMessage>? Chat = null);

public interface IHighlightDetector
{
    IEnumerable<Highlight> Detect(DetectionContext context);
}
