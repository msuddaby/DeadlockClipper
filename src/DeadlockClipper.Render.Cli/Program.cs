// Render highlights from a demo without the web app. Used to smoke-test the render pipeline end to end.
//
//   dotnet run --project src/DeadlockClipper.Render.Cli -- <demo.dem> [--highlight <id>] [--player <steamId>]
//       [--game-dir <Deadlock/game/citadel>] [--out <dir>]
//
// With neither --highlight nor --player, every highlight in the demo is rendered.

using DeadlockClipper.Core.Parsing;
using DeadlockClipper.Render;
using Microsoft.Extensions.Logging;

var demoPath = args.FirstOrDefault(a => !a.StartsWith("--")) ?? Usage();
string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();

var gameDir = Arg("--game-dir") ?? GuessGameDir(demoPath)
              ?? throw new ArgumentException("Pass --game-dir <…/Deadlock/game/citadel>.");
var outDir = Arg("--out") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos", "Deadlock Clips");

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; }));
var log = loggerFactory.CreateLogger("cli");

log.LogInformation("Parsing {Demo}", demoPath);
var analysis = await new DemoAnalyzer().AnalyzeFileAsync(demoPath);
var highlights = analysis.Highlights
    .Where(h => Arg("--highlight") is not { } id || h.Id == id)
    .Where(h => Arg("--player") is not { } p || h.PlayerSteamId.ToString() == p)
    .ToList();
if (highlights.Count == 0)
{
    log.LogError("No matching highlights. Available: {Ids}", string.Join(", ", analysis.Highlights.Select(h => $"{h.Id} ({h.Type}, {h.PlayerName})")));
    return 1;
}

var options = new RenderOptions
{
    GameDir = gameDir,
    OutputDir = outDir,
    WorkDir = Path.Combine(Path.GetTempPath(), "deadlock-clipper-work"),
};
var demoKey = Path.GetFileNameWithoutExtension(demoPath);
var jobs = highlights.Select(h => CaptionPlan.CreateJob(
    jobId: Guid.NewGuid().ToString("N")[..8],
    demoKey: demoKey,
    demoPath: demoPath,
    analysis: analysis,
    highlight: h,
    outputPath: Path.Combine(outDir, $"{analysis.MatchId ?? 0}_{h.PlayerName.Split(' ')[0]}_{h.Type}_{h.StartTick}.mp4")))
    .ToList();

var runner = new ProcessRunner();
var game = new GameSession(options, runner, loggerFactory.CreateLogger<GameSession>());
var stager = new DemoStager(options);
var renderer = new Renderer(options, game, new Recorder(options, runner), new ClipFinisher(options, runner),
    stager.StageAsync, loggerFactory.CreateLogger<Renderer>());

var progress = new Progress<RenderProgress>(p => log.LogInformation("[{Job}] {Stage} {Detail}", p.JobId, p.Stage, p.Detail));
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    var results = await renderer.RenderBatchAsync(jobs, progress, cts.Token);
    foreach (var r in results)
        Console.WriteLine(r.Success ? $"OK    {r.OutputPath}" : $"FAIL  {r.JobId}: {r.Error}");
    return results.All(r => r.Success) ? 0 : 2;
}
finally
{
    await game.QuitAsync(CancellationToken.None);
    stager.Cleanup();
}

static string Usage()
{
    Console.Error.WriteLine("usage: <demo.dem> [--highlight <id>] [--player <steamId>] [--game-dir <dir>] [--out <dir>]");
    Environment.Exit(64);
    return "";
}

// Demos in the game's replays folder sit at …/game/citadel/replays/<id>.dem.
static string? GuessGameDir(string demo)
{
    var dir = Path.GetDirectoryName(Path.GetFullPath(demo));
    return dir != null && Path.GetFileName(dir) == "replays" ? Path.GetDirectoryName(dir) : null;
}
