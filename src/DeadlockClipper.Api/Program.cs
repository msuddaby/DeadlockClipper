using DeadlockClipper.Api;
using DeadlockClipper.Api.Data;
using DeadlockClipper.Api.Heroes;
using DeadlockClipper.Api.Jobs;
using DeadlockClipper.Api.Storage;
using DeadlockClipper.Core.Highlights;
using DeadlockClipper.Core.Parsing;
using Microsoft.EntityFrameworkCore;

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
builder.Services.AddSingleton<HeroCatalog>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HeroCatalog>());

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
}

app.MapClipperApi();

// Serve the built frontend (web/dist copied to wwwroot) when not using the Vite dev server.
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();
