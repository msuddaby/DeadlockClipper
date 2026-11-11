using DeadlockClipper.Render;

namespace DeadlockClipper.Render.Tests;

public class ParsingTests
{
    [Fact]
    public void LeadIn_IsFreezeAtStart()
    {
        const string log = """
            [Parsed_freezedetect_0 @ 0x1] lavfi.freezedetect.freeze_start: 0.016738
            [Parsed_freezedetect_0 @ 0x1] lavfi.freezedetect.freeze_duration: 1.59999
            [Parsed_freezedetect_0 @ 0x1] lavfi.freezedetect.freeze_end: 1.616728
            """;
        Assert.Equal(1.616728, ClipFinisher.ParseLeadIn(log), 5);
    }

    [Fact]
    public void LeadIn_IgnoresFreezeLaterInClip()
    {
        const string log = """
            lavfi.freezedetect.freeze_start: 2.5
            lavfi.freezedetect.freeze_end: 3.1
            """;
        Assert.Equal(0, ClipFinisher.ParseLeadIn(log));
    }

    [Fact]
    public void LeadIn_NoFreeze() => Assert.Equal(0, ClipFinisher.ParseLeadIn("nothing here"));

    [Fact]
    public void SinkInputs_FindsOnlyDeadlockStreams()
    {
        const string json = """
            [{"index":747,"properties":{"application.name":"Firefox"}},
             {"index":3487,"properties":{"application.name":"deadlock.exe"}}]
            """;
        Assert.Equal([3487], GameSession.ParseGameSinkInputs(json));
    }
}
