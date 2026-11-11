// Talk to the Deadlock Game Coordinator directly with a (secondary) Steam account.
//
//   dotnet run --project src/DeadlockClipper.Steam.Cli -- login
//   dotnet run --project src/DeadlockClipper.Steam.Cli -- history <steamId64|accountId>
//   dotnet run --project src/DeadlockClipper.Steam.Cli -- salts <matchId>
//   dotnet run --project src/DeadlockClipper.Steam.Cli -- meta <matchId>
//   dotnet run --project src/DeadlockClipper.Steam.Cli -- download <matchId> [out.dem]
//
// Options: --account <name>  (which saved account to use; default the first)
//          --accounts-dir <path>  (default ~/.config/deadlock-clipper/steam-accounts)
// Accounts can also be added from the app (Matches → Clipper accounts).

using DeadlockClipper.Steam;
using Microsoft.Extensions.Logging;
using SteamKit2.Authentication;

var positional = new List<string>();
string? accountName = null, accountsDir = null;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--account" && i + 1 < args.Length) accountName = args[++i];
    else if (args[i] == "--accounts-dir" && i + 1 < args.Length) accountsDir = args[++i];
    else positional.Add(args[i]);
}
var store = new SteamAccountStore(accountsDir ?? SteamAccountStore.DefaultDirectory);
if (positional.Count == 0) return Usage();

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; }));
var log = loggerFactory.CreateLogger("steam");
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;

await using var gc = new DeadlockGc(loggerFactory.CreateLogger<DeadlockGc>());

try
{
    switch (positional[0])
    {
        case "login":
        {
            Console.Write("Steam username: ");
            var user = Console.ReadLine()?.Trim() ?? "";
            var password = ReadPassword("Password: ");
            var token = await gc.LoginWithPasswordAsync(user, password, new UserConsoleAuthenticator(), store.Get(user)?.GuardData, ct);
            store.Save(token);
            Console.WriteLine($"Saved login for {token.AccountName} in {store.Directory}");
            await gc.ConnectAsync(token, ct);
            Console.WriteLine("Game Coordinator connection works.");
            return 0;
        }
        case "history" when positional.Count > 1:
        {
            await ConnectAsync();
            var accountId = ToAccountId(positional[1]);
            var page = await gc.GetMatchHistoryAsync(accountId, 0, ct);
            Console.WriteLine($"{"match",-12} {"start (local)",-17} {"dur",6} {"hero",5} {"K/D/A",-9} {"souls",7}");
            foreach (var m in page.Matches)
                Console.WriteLine($"{m.MatchId,-12} {DateTimeOffset.FromUnixTimeSeconds(m.StartTime).ToLocalTime():yyyy-MM-dd HH:mm} " +
                                  $"{m.MatchDurationS / 60,3}:{m.MatchDurationS % 60:00} {m.HeroId,5} " +
                                  $"{$"{m.PlayerKills}/{m.PlayerDeaths}/{m.PlayerAssists}",-9} {m.NetWorth,7}");
            Console.WriteLine($"{page.Matches.Count} matches (cursor {page.ContinueCursor})");
            return 0;
        }
        case "salts" when positional.Count > 1:
        {
            await ConnectAsync();
            var salts = await gc.GetMatchSaltsAsync(ulong.Parse(positional[1]), 0, ct);
            Console.WriteLine($"cluster {salts.ClusterId}, replay valid through {salts.ReplayValidThrough?.ToLocalTime():yyyy-MM-dd HH:mm}");
            Console.WriteLine($"demo:     {salts.DemoUrl}");
            Console.WriteLine($"metadata: {salts.MetadataUrl}");
            return 0;
        }
        case "meta" when positional.Count > 1:
        {
            await ConnectAsync();
            var salts = await gc.GetMatchSaltsAsync(ulong.Parse(positional[1]), 0, ct);
            using var http = new HttpClient();
            var meta = await new ReplayDownloader(http).GetMetadataAsync(salts, ct);
            var info = meta.MatchInfo;
            Console.WriteLine($"match {info.MatchId}: {info.DurationS / 60}:{info.DurationS % 60:00}, {info.Players.Count} players");
            var bySlot = info.Players.ToDictionary(p => p.PlayerSlot);
            foreach (var p in info.Players.OrderBy(p => p.Team).ThenBy(p => p.PlayerSlot))
            {
                var killTimes = info.Players.SelectMany(v => v.DeathDetails)
                    .Where(d => d.KillerPlayerSlot == p.PlayerSlot)
                    .Select(d => d.GameTimeS).Order().ToList();
                Console.WriteLine($"  slot {p.PlayerSlot,2} {p.Team,-22} acct {p.AccountId,-10} hero {p.HeroId,3} " +
                                  $"{p.Kills}/{p.Deaths}/{p.Assists}  kill times: {string.Join(' ', killTimes.Select(t => $"{t / 60}:{t % 60:00}"))}");
            }
            return 0;
        }
        case "download" when positional.Count > 1:
        {
            await ConnectAsync();
            var matchId = ulong.Parse(positional[1]);
            var salts = await gc.GetMatchSaltsAsync(matchId, 0, ct);
            var output = positional.Count > 2 ? positional[2] : $"{matchId}.dem";
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var last = -1;
            await new ReplayDownloader(http).DownloadDemoAsync(salts, output, new Progress<double>(f =>
            {
                var pct = (int)(f * 100);
                if (pct / 10 != last / 10) Console.WriteLine($"  {pct}%");
                last = pct;
            }), ct);
            Console.WriteLine($"Saved {output} ({new FileInfo(output).Length / 1024 / 1024} MB)");
            return 0;
        }
        default:
            return Usage();
    }
}
catch (Exception ex) when (ex is SteamLoginException or GcRequestException or FileNotFoundException or AuthenticationException)
{
    log.LogError("{Message}", ex.Message);
    return 1;
}

async Task ConnectAsync()
{
    var token = (accountName != null ? store.Get(accountName) : store.List().FirstOrDefault())
                ?? throw new SteamLoginException($"No saved login{(accountName != null ? $" for {accountName}" : "")} in {store.Directory}; run `login` first.");
    await gc.ConnectAsync(token, ct);
}

static uint ToAccountId(string id) => MetadataAnalysis.ToAccountId(ulong.Parse(id));

static string ReadPassword(string prompt)
{
    Console.Write(prompt);
    var chars = new List<char>();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); }
        else if (!char.IsControl(key.KeyChar)) chars.Add(key.KeyChar);
    }
    Console.WriteLine();
    return new string(chars.ToArray());
}

static int Usage()
{
    Console.Error.WriteLine("usage: login | history <steamId> | salts <matchId> | meta <matchId> | download <matchId> [out.dem]  [--account <name>] [--accounts-dir <path>]");
    return 64;
}
