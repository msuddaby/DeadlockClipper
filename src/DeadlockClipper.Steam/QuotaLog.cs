using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeadlockClipper.Steam;

// TEMPORARY: measuring Valve's undocumented Game Coordinator rate limit (see TODO.md). Remove once the limit is known
// and MetadataIntervalSeconds/backoff are tuned to it.

public enum QuotaRequest
{
    Metadata,
    History,
}

public enum QuotaOutcome
{
    Ok,
    RateLimited,
    // Answered, but not with data (invalid match, permission, …): the request still reached the GC.
    Refused,
    // No answer (disconnect, timeout): may not have reached the GC.
    Failed,
}

public sealed record QuotaEntry(DateTimeOffset At, string Account, QuotaRequest Request, QuotaOutcome Outcome, string? Detail = null);

/// <summary>Appends every Game Coordinator request and its outcome to a JSON-lines file.</summary>
public sealed class QuotaLog(string path)
{
    internal static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly object _sync = new();

    public string Path { get; } = path;

    public void Record(string account, QuotaRequest request, QuotaOutcome outcome, string? detail = null)
    {
        var line = JsonSerializer.Serialize(new QuotaEntry(DateTimeOffset.UtcNow, account, request, outcome, detail), Json);
        lock (_sync)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
            File.AppendAllText(Path, line + "\n");
        }
    }

    public IReadOnlyList<QuotaEntry> Read()
    {
        if (!File.Exists(Path)) return [];
        string[] lines;
        lock (_sync) lines = File.ReadAllLines(Path);
        var entries = new List<QuotaEntry>();
        foreach (var l in lines)
        {
            try
            {
                if (JsonSerializer.Deserialize<QuotaEntry>(l, Json) is { } e) entries.Add(e);
            }
            catch (JsonException) { /* partial line from a crash */ }
        }
        return entries;
    }
}

/// <summary>What one rate-limit episode says about the quota.</summary>
/// <param name="OkBefore">Successful metadata requests in the window before the first refusal, keyed by window ("30m", "1h", "3h", "24h").</param>
/// <param name="RefusedAttempts">Rate-limited attempts during the episode (they might extend it).</param>
/// <param name="RecoveredAt">First success after the episode; null if still limited.</param>
public sealed record LimitEpisode(
    DateTimeOffset FirstRefusal,
    IReadOnlyDictionary<string, int> OkBefore,
    int RefusedAttempts,
    DateTimeOffset? RecoveredAt,
    double? BlockedMinutes,
    // How many successes in a row came after recovery before the next limit (or so far).
    int OkAfterRecovery);

public sealed record AccountQuota(
    string Account,
    int MetadataOk,
    int MetadataRateLimited,
    int HistoryOk,
    int HistoryRateLimited,
    // Highest number of successful metadata requests seen inside any window of this length: a lower bound on the quota.
    IReadOnlyDictionary<string, int> MaxOkInWindow,
    IReadOnlyList<LimitEpisode> Episodes);

public sealed record QuotaReport(string LogPath, DateTimeOffset? Since, IReadOnlyList<AccountQuota> Accounts);

public static class QuotaAnalysis
{
    private static readonly (string Name, TimeSpan Length)[] Windows =
    [
        ("30m", TimeSpan.FromMinutes(30)),
        ("1h", TimeSpan.FromHours(1)),
        ("3h", TimeSpan.FromHours(3)),
        ("24h", TimeSpan.FromHours(24)),
    ];

    public static QuotaReport Analyze(string logPath, IReadOnlyList<QuotaEntry> entries)
    {
        var accounts = entries.GroupBy(e => e.Account, StringComparer.OrdinalIgnoreCase)
            .Select(g => Account(g.Key, g.OrderBy(e => e.At).ToList()))
            .OrderBy(a => a.Account, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new QuotaReport(logPath, entries.Count > 0 ? entries.Min(e => e.At) : null, accounts);
    }

    private static AccountQuota Account(string name, List<QuotaEntry> all)
    {
        var meta = all.Where(e => e.Request == QuotaRequest.Metadata).ToList();
        var okTimes = meta.Where(e => e.Outcome == QuotaOutcome.Ok).Select(e => e.At).ToList();

        var episodes = new List<LimitEpisode>();
        for (var i = 0; i < meta.Count; i++)
        {
            if (meta[i].Outcome != QuotaOutcome.RateLimited) continue;
            // An episode starts at a refusal that follows a success (or the start of the log).
            if (i > 0 && meta[i - 1].Outcome == QuotaOutcome.RateLimited) continue;

            var start = meta[i].At;
            var refused = meta.Skip(i).TakeWhile(e => e.Outcome != QuotaOutcome.Ok).Count(e => e.Outcome == QuotaOutcome.RateLimited);
            var recovered = meta.Skip(i).FirstOrDefault(e => e.Outcome == QuotaOutcome.Ok)?.At;
            var okAfter = recovered is { } r
                ? meta.SkipWhile(e => e.At < r).TakeWhile(e => e.Outcome != QuotaOutcome.RateLimited).Count(e => e.Outcome == QuotaOutcome.Ok)
                : 0;
            episodes.Add(new LimitEpisode(
                start,
                Windows.ToDictionary(w => w.Name, w => okTimes.Count(t => t < start && t >= start - w.Length)),
                refused,
                recovered,
                recovered is { } rec ? Math.Round((rec - start).TotalMinutes, 1) : null,
                okAfter));
        }

        return new AccountQuota(
            name,
            okTimes.Count,
            meta.Count(e => e.Outcome == QuotaOutcome.RateLimited),
            all.Count(e => e.Request == QuotaRequest.History && e.Outcome == QuotaOutcome.Ok),
            all.Count(e => e.Request == QuotaRequest.History && e.Outcome == QuotaOutcome.RateLimited),
            Windows.ToDictionary(w => w.Name, w => MaxInWindow(okTimes, w.Length)),
            episodes);
    }

    /// <summary>Sliding window over sorted times: the most that fit inside any span of <paramref name="length"/>.</summary>
    private static int MaxInWindow(List<DateTimeOffset> sorted, TimeSpan length)
    {
        int best = 0, lo = 0;
        for (var hi = 0; hi < sorted.Count; hi++)
        {
            while (sorted[hi] - sorted[lo] >= length) lo++;
            best = Math.Max(best, hi - lo + 1);
        }
        return best;
    }
}
