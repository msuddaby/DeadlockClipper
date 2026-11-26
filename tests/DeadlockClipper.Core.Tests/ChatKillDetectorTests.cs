using DeadlockClipper.Core.Highlights;
using DeadlockClipper.Core.Parsing;

namespace DeadlockClipper.Core.Tests;

public class ChatKillDetectorTests
{
    private const ulong Me = 1, Mate = 2, Enemy1 = 7, Enemy2 = 8;
    private const int Rate = 64;

    private static KillEvent Kill(double seconds, ulong attacker, ulong victim, params ulong[] assisters) =>
        new((int)(seconds * Rate), seconds, attacker, "", 0, victim, "", 0, "", assisters);

    private static ChatMessage Say(double seconds, ulong sender, string text = "wtf") =>
        new((int)(seconds * Rate), seconds, sender, "", true, text);

    private static DetectionContext Context(KillEvent[] kills, params ChatMessage[] chat) => new(Rate, 200_000,
        new ulong[] { Me, Mate, Enemy1, Enemy2 }
            .Select(id => new PlayerInfo(id, $"p{id}", id < 7 ? Team.Sapphire : Team.Amber, (int)id, 0, 0, 0, 0)).ToList(),
        kills, chat);

    [Fact]
    public void SingleKill_WithVictimChatSoonAfter_IsAHighlight()
    {
        var kill = Kill(600, Me, Enemy1);
        var h = Assert.Single(new ChatKillDetector().Detect(Context([kill], Say(606, Enemy1))));

        Assert.Equal(HighlightType.ChatKill, h.Type);
        Assert.Equal(Me, h.PlayerSteamId);
        Assert.Equal($"{Me}-chat-{kill.Tick}", h.Id);
        Assert.Equal([kill], h.Kills);
        Assert.Equal(kill.Tick - 5 * Rate, h.ClipStartTick);
    }

    [Fact]
    public void IgnoresChatFromOthers_BeforeTheKill_OrTooLate()
    {
        KillEvent[] kills = [Kill(600, Me, Enemy1)];
        Assert.Empty(new ChatKillDetector().Detect(Context(kills, Say(605, Enemy2), Say(599, Enemy1), Say(621, Enemy1))));
        Assert.Empty(new ChatKillDetector().Detect(Context(kills)));
    }

    [Fact]
    public void KillsInsideMyMultikill_AreDropped_OthersKept()
    {
        KillEvent[] kills = [Kill(600, Me, Enemy1), Kill(604, Me, Enemy2), Kill(900, Me, Enemy1)];
        var context = Context(kills, Say(602, Enemy1), Say(905, Enemy1));
        var all = new MultiKillDetector().Detect(context).Concat(new ChatKillDetector().Detect(context)).ToList();

        var kept = ChatKillDetector.RemoveCovered(all).ToList();

        Assert.Single(kept, h => h.Type == HighlightType.DoubleKill);
        var chatKill = Assert.Single(kept, h => h.Type == HighlightType.ChatKill);
        Assert.Equal(kills[2].Tick, chatKill.StartTick);
    }

    [Fact]
    public void MoreReplies_ScoreHigher_ButStayBelowADoubleKill()
    {
        KillEvent[] kills = [Kill(600, Me, Enemy1)];
        var one = Assert.Single(new ChatKillDetector().Detect(Context(kills, Say(601, Enemy1))));
        var many = Assert.Single(new ChatKillDetector().Detect(Context(kills, Say(601, Enemy1), Say(603, Enemy1), Say(605, Enemy1), Say(607, Enemy1))));

        Assert.True(many.Score > one.Score);
        Assert.True(many.Score < 6);
    }
}
