using DeadlockClipper.Api;
using DeadlockClipper.Api.Data;
using DeadlockClipper.Api.Heroes;
using DeadlockClipper.Api.Jobs;
using DeadlockClipper.Api.Storage;
using DeadlockClipper.Core.Highlights;
using DeadlockClipper.Core.Parsing;
using DeadlockClipper.Render;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ClipperOptions>(builder.Configuration.GetSection(ClipperOptions.Section));
builder.Services.ConfigureHttpJsonOptions(o => Json.Configure(o.SerializerOptions));
builder.Services.AddSingleton(Json.Create()); // for the stored analysis JSON

// Uploads set their own per-request limit (see Endpoints.UploadAsync).
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 30 * 1024 * 1024);

builder.Services.AddSingleton<DemoStorage>();
builder.Services.AddDbContext<AppDbContext>((sp, o) =>
{
    var storage = sp.GetRequiredService<DemoStorage>();
    o.UseSqlite($"Data Source={Path.Combine(storage.DataDir, "clipper.db")}");
});

// Register every highlight detector here; DemoAnalyzer receives them all via IEnumerable<IHighlightDetector>.
builder.Services.AddSingleton<IHighlightDetector>(_ => new MultiKillDetector());
builder.Services.AddSingleton<DemoAnalyzer>();
builder.Services.AddSingleton<ParseQueue>();
builder.Services.AddHostedService<ParseWorker>();

builder.Services.AddHttpClient();
// Valve's replay servers; demos are hundreds of MB, so no overall timeout.
builder.Services.AddHttpClient("replays", c => c.Timeout = Timeout.InfiniteTimeSpan);

// Match scanning and replay downloads go through secondary Steam accounts' Game Coordinator sessions.
builder.Services.AddSingleton(builder.Configuration.GetSection($"{ClipperOptions.Section}:Steam").Get<DeadlockClipper.Steam.SteamOptions>()
                              ?? new DeadlockClipper.Steam.SteamOptions());
// TEMPORARY: logs every GC request so the undocumented rate limit can be measured (GET /api/steam/quota).
builder.Services.AddSingleton(sp => new DeadlockClipper.Steam.QuotaLog(
    Path.Combine(sp.GetRequiredService<DemoStorage>().DataDir, "gc-quota-log.jsonl")));
builder.Services.AddSingleton<DeadlockClipper.Steam.GcPool>();
builder.Services.AddSingleton<DeadlockClipper.Steam.SteamLogins>();
builder.Services.AddSingleton<MatchScanQueue>();
builder.Services.AddHostedService<MatchScanWorker>();
builder.Services.AddSingleton<DownloadQueue>();
builder.Services.AddHostedService<DownloadWorker>();
builder.Services.AddSingleton<HeroCatalog>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HeroCatalog>());

// Rendering: drives a headless Deadlock (see tools/deadlock-launch-wrapper.sh) one demo batch at a time.
builder.Services.AddSingleton(sp =>
{
    var render = builder.Configuration.GetSection($"{ClipperOptions.Section}:Render").Get<RenderOptions>() ?? new RenderOptions();
    var clipper = sp.GetRequiredService<IOptions<ClipperOptions>>().Value;
    var storage = sp.GetRequiredService<DemoStorage>();
    if (string.IsNullOrWhiteSpace(render.GameDir) && !string.IsNullOrWhiteSpace(clipper.ReplaysDir))
        render.GameDir = Path.GetDirectoryName(Path.GetFullPath(clipper.ReplaysDir).TrimEnd('/'))!;
    if (string.IsNullOrWhiteSpace(render.OutputDir))
        render.OutputDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos", "Deadlock Clips");
    if (string.IsNullOrWhiteSpace(render.WorkDir))
        render.WorkDir = Path.Combine(storage.DataDir, "render-work");
    if (string.IsNullOrWhiteSpace(render.AssetCacheDir))
        render.AssetCacheDir = Path.Combine(storage.DataDir, "hero-art");
    return render;
});
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<IGameSession, GameSession>();
builder.Services.AddSingleton<IRecorder, Recorder>();
builder.Services.AddSingleton<DeadlockClipper.Render.Overlays.IOverlayArtist, DeadlockClipper.Render.Overlays.OverlayArtist>();
builder.Services.AddSingleton<IClipFinisher>(sp => new ClipFinisher(
    sp.GetRequiredService<RenderOptions>(),
    sp.GetRequiredService<IProcessRunner>(),
    sp.GetRequiredService<DeadlockClipper.Render.Overlays.IOverlayArtist>()));
builder.Services.AddSingleton<DemoStager>();
builder.Services.AddSingleton(sp => new Renderer(
    sp.GetRequiredService<RenderOptions>(),
    sp.GetRequiredService<IGameSession>(),
    sp.GetRequiredService<IRecorder>(),
    sp.GetRequiredService<IClipFinisher>(),
    sp.GetRequiredService<DemoStager>().StageAsync,
    sp.GetRequiredService<ILogger<Renderer>>()));
builder.Services.AddSingleton<RenderQueue>();
builder.Services.AddSingleton<RenderHealth>();
builder.Services.AddHostedService<RenderWorker>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    SchemaUpgrade.Apply(scope.ServiceProvider.GetRequiredService<AppDbContext>());
}

app.MapClipperApi();
app.MapRenderApi();
app.MapMatchApi();

// Serve the built frontend (web/dist copied to wwwroot) when not using the Vite dev server.
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();
