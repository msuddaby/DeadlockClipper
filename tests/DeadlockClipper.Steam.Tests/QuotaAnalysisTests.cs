using DeadlockClipper.Steam;

namespace DeadlockClipper.Steam.Tests;

public class QuotaAnalysisTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private static QuotaEntry E(double minutes, QuotaOutcome outcome, string account = "bot") =>
        new(T0.AddMinutes(minutes), account, QuotaRequest.Metadata, outcome);

    [Fact]
    public void Episode_CountsSuccessesBefore_AndTimesTheRecovery()
    {
        var log = new List<QuotaEntry>();
        // 20 successes two minutes apart (t = 0..38), then refused at 40, 45, 50; back at 55; 3 more successes; refused again.
        for (var i = 0; i < 20; i++) log.Add(E(i * 2, QuotaOutcome.Ok));
        log.AddRange([E(40, QuotaOutcome.RateLimited), E(45, QuotaOutcome.RateLimited), E(50, QuotaOutcome.RateLimited)]);
        log.AddRange([E(55, QuotaOutcome.Ok), E(57, QuotaOutcome.Ok), E(59, QuotaOutcome.Ok), E(61, QuotaOutcome.RateLimited)]);

        var a = Assert.Single(QuotaAnalysis.Analyze("x", log).Accounts);

        Assert.Equal(23, a.MetadataOk);
        Assert.Equal(4, a.MetadataRateLimited);
        Assert.Equal(2, a.Episodes.Count);

        var first = a.Episodes[0];
        Assert.Equal(T0.AddMinutes(40), first.FirstRefusal);
        Assert.Equal(15, first.OkBefore["30m"]); // t = 10..38
        Assert.Equal(20, first.OkBefore["1h"]);
        Assert.Equal(3, first.RefusedAttempts);
        Assert.Equal(15, first.BlockedMinutes);
        Assert.Equal(3, first.OkAfterRecovery);

        Assert.Null(a.Episodes[1].RecoveredAt);
        Assert.Equal(23, a.MaxOkInWindow["1h"]); // t = 0..59: all of them
    }

    [Fact]
    public void Log_RoundTripsThroughTheFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"quota-{Guid.NewGuid():N}.jsonl");
        try
        {
            var log = new QuotaLog(path);
            log.Record("bot", QuotaRequest.Metadata, QuotaOutcome.Ok);
            log.Record("bot", QuotaRequest.History, QuotaOutcome.RateLimited, "Rate limited");
            File.AppendAllText(path, """{"At":"""); // a torn last line is skipped

            var entries = log.Read();

            Assert.Equal([QuotaOutcome.Ok, QuotaOutcome.RateLimited], entries.Select(e => e.Outcome));
            Assert.Equal(QuotaRequest.History, entries[1].Request);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
