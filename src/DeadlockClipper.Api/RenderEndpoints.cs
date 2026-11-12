using System.Text.Json;
using DeadlockClipper.Api.Data;
using DeadlockClipper.Api.Jobs;
using DeadlockClipper.Core.Highlights;
using DeadlockClipper.Core.Parsing;
using DeadlockClipper.Render;
using Microsoft.EntityFrameworkCore;

namespace DeadlockClipper.Api;

public sealed record RenderDto(
    Guid Id,
    Guid DemoId,
    string HighlightId,
    RenderStatus Status,
    RenderStage? Stage,
    string? Detail,
    string? Error,
    string PlayerName,
    string HighlightType,
    int HeroId,
    double GameTimeSeconds,
    long? MatchId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt,
    bool HasVideo);

public sealed record RenderPlayerRequest(string SteamId);

public static class RenderEndpoints
{
    public static void MapRenderApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/render/health", (RenderHealth health, CancellationToken ct) => health.CheckAsync(ct));

        api.MapGet("/renders", async (Guid? demoId, AppDbContext db, RenderQueue queue) =>
        {
            var query = db.Renders.AsQueryable();
            if (demoId is { } d) query = query.Where(r => r.DemoId == d);
            var renders = await query.OrderByDescending(r => r.CreatedAt).ToListAsync();
            return renders.Select(r => ToDto(r, queue));
        });

        api.MapPost("/demos/{id:guid}/highlights/{highlightId}/render",
            async (Guid id, string highlightId, AppDbContext db, RenderQueue queue, JsonSerializerOptions json) =>
            {
                var (analysis, error) = await LoadAnalysisAsync(id, db, json);
                if (analysis == null) return error!;
                var highlight = analysis.Highlights.FirstOrDefault(h => h.Id == highlightId);
                if (highlight == null) return Results.NotFound();

                var render = await EnqueueAsync(id, highlight, analysis, db);
                queue.Notify();
                return Results.Ok(ToDto(render, queue));
            });

        api.MapPost("/demos/{id:guid}/render-player",
            async (Guid id, RenderPlayerRequest req, AppDbContext db, RenderQueue queue, JsonSerializerOptions json) =>
            {
                var (analysis, error) = await LoadAnalysisAsync(id, db, json);
                if (analysis == null) return error!;
                if (!ulong.TryParse(req.SteamId, out var steamId)) return Results.Problem("Invalid steamId.", statusCode: 400);

                var renders = new List<ClipRender>();
                foreach (var h in analysis.Highlights.Where(h => h.PlayerSteamId == steamId).OrderBy(h => h.StartTick))
                    renders.Add(await EnqueueAsync(id, h, analysis, db));
                queue.Notify();
                return Results.Ok(renders.Select(r => ToDto(r, queue)));
            });

        api.MapPost("/demos/{id:guid}/players/{steamId}/compilation",
            async (Guid id, string steamId, AppDbContext db, RenderQueue queue, JsonSerializerOptions json) =>
            {
                var (analysis, error) = await LoadAnalysisAsync(id, db, json);
                if (analysis == null) return error!;
                if (!ulong.TryParse(steamId, out var player)) return Results.Problem("Invalid steamId.", statusCode: 400);
                if (analysis.Players.All(p => p.SteamId != player)) return Results.NotFound();

                var compilation = KillCompilation.Build(analysis, player);
                if (compilation == null) return Results.Problem("This player needs at least 2 hero kills for a compilation.", statusCode: 409);

                var render = await EnqueueAsync(id, compilation, analysis, db);
                queue.Notify();
                return Results.Ok(ToDto(render, queue));
            });

        api.MapGet("/renders/{id:guid}/video", async (Guid id, AppDbContext db) =>
        {
            var r = await db.Renders.FindAsync(id);
            if (r?.OutputPath is null || !File.Exists(r.OutputPath)) return Results.NotFound();
            // Range processing lets the browser's <video> element seek.
            return Results.File(r.OutputPath, "video/mp4", Path.GetFileName(r.OutputPath), enableRangeProcessing: true);
        });

        api.MapDelete("/renders/{id:guid}", async (Guid id, AppDbContext db) =>
        {
            var r = await db.Renders.FindAsync(id);
            if (r == null) return Results.NotFound();
            if (r.Status == RenderStatus.Rendering) return Results.Problem("This clip is rendering right now.", statusCode: 409);
            db.Renders.Remove(r);
            await db.SaveChangesAsync();
            if (r.OutputPath != null && File.Exists(r.OutputPath)) File.Delete(r.OutputPath);
            return Results.NoContent();
        });
    }

    private static async Task<(MatchAnalysis?, IResult?)> LoadAnalysisAsync(Guid demoId, AppDbContext db, JsonSerializerOptions json)
    {
        var demo = await db.Demos.FindAsync(demoId);
        if (demo == null) return (null, Results.NotFound());
        if (demo.Status != DemoStatus.Done || demo.AnalysisJson == null)
            return (null, Results.Problem("This demo hasn't finished parsing yet.", statusCode: 409));
        if (demo.AnalysisVersion < DemoAnalyzer.Version)
            return (null, Results.Problem("This demo was parsed by an older version; re-parse it first.", statusCode: 409));
        return (JsonSerializer.Deserialize<MatchAnalysis>(demo.AnalysisJson, json), null);
    }

    /// <summary>Returns the existing render for this highlight unless it failed (then it's retried).</summary>
    internal static async Task<ClipRender> EnqueueAsync(Guid demoId, Highlight h, MatchAnalysis analysis, AppDbContext db)
    {
        var existing = await db.Renders.FirstOrDefaultAsync(r => r.DemoId == demoId && r.HighlightId == h.Id);
        if (existing is { Status: not RenderStatus.Failed }) return existing;

        var render = existing ?? new ClipRender
        {
            Id = Guid.NewGuid(),
            DemoId = demoId,
            HighlightId = h.Id,
            PlayerName = h.PlayerName,
            HighlightType = h.Type.ToString(),
            HeroId = h.HeroId,
            GameTimeSeconds = h.StartTimeSeconds,
            MatchId = analysis.MatchId,
        };
        render.Status = RenderStatus.Queued;
        render.Error = null;
        render.CreatedAt = DateTimeOffset.UtcNow;
        render.FinishedAt = null;
        if (existing == null) db.Renders.Add(render);
        await db.SaveChangesAsync();
        return render;
    }

    private static RenderDto ToDto(ClipRender r, RenderQueue queue)
    {
        var live = r.Status is RenderStatus.Queued or RenderStatus.Rendering ? queue.Live(r.Id) : null;
        return new RenderDto(r.Id, r.DemoId, r.HighlightId, r.Status, live?.Stage, live?.Detail, r.Error,
            r.PlayerName, r.HighlightType, r.HeroId, r.GameTimeSeconds, r.MatchId, r.CreatedAt, r.FinishedAt,
            r.Status == RenderStatus.Done && r.OutputPath != null && File.Exists(r.OutputPath));
    }
}
