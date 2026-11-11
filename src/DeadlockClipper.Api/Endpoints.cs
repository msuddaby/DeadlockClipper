using System.Text.Json;
using DeadlockClipper.Api.Data;
using DeadlockClipper.Api.Heroes;
using DeadlockClipper.Api.Jobs;
using DeadlockClipper.Api.Storage;
using DeadlockClipper.Core.Parsing;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace DeadlockClipper.Api;

public sealed record DemoSummaryDto(
    Guid Id,
    string FileName,
    DemoSource Source,
    long SizeBytes,
    long? MatchId,
    DemoStatus Status,
    double? Progress,
    string? Error,
    string? Map,
    double? DurationSeconds,
    int? HighlightCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ParsedAt);

public sealed record DemoDetailDto(DemoSummaryDto Demo, MatchAnalysis? Analysis);

public sealed record LocalReplayDto(string FileName, long SizeBytes, DateTimeOffset ModifiedAt, Guid? ImportedDemoId);

public sealed record ImportLocalRequest(string FileName);

public static class Endpoints
{
    public static void MapClipperApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/demos", async (AppDbContext db, ParseQueue queue) =>
        {
            var demos = await db.Demos.OrderByDescending(d => d.CreatedAt).ToListAsync();
            return demos.Select(d => ToSummary(d, queue));
        });

        api.MapGet("/demos/{id:guid}", async (Guid id, AppDbContext db, ParseQueue queue, JsonSerializerOptions json) =>
        {
            var d = await db.Demos.FindAsync(id);
            if (d is null) return Results.NotFound();
            var analysis = d.AnalysisJson is null ? null : JsonSerializer.Deserialize<MatchAnalysis>(d.AnalysisJson, json);
            return Results.Ok(new DemoDetailDto(ToSummary(d, queue), analysis));
        });

        api.MapPost("/demos", UploadAsync).DisableAntiforgery();

        api.MapPost("/demos/{id:guid}/reparse", async (Guid id, AppDbContext db, ParseQueue queue) =>
        {
            var d = await db.Demos.FindAsync(id);
            if (d is null) return Results.NotFound();
            d.Status = DemoStatus.Queued;
            d.Error = null;
            await db.SaveChangesAsync();
            queue.Enqueue(d.Id);
            return Results.Accepted($"/api/demos/{d.Id}", ToSummary(d, queue));
        });

        api.MapDelete("/demos/{id:guid}", async (Guid id, AppDbContext db, DemoStorage storage) =>
        {
            var d = await db.Demos.FindAsync(id);
            if (d is null) return Results.NotFound();
            db.Demos.Remove(d);
            await db.SaveChangesAsync();
            if (d.Source != DemoSource.LocalReplay) storage.DeleteIfOwned(d.FilePath);
            return Results.NoContent();
        });

        api.MapGet("/local-replays", async (IOptions<ClipperOptions> options, AppDbContext db) =>
        {
            var dir = options.Value.ReplaysDir;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                return Results.Ok(new { replaysDir = dir, exists = false, replays = Array.Empty<LocalReplayDto>() });

            var files = new DirectoryInfo(dir).GetFiles("*.dem").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            var paths = files.Select(f => f.FullName).ToList();
            var imported = await db.Demos.Where(d => paths.Contains(d.FilePath)).Select(d => new { d.FilePath, d.Id }).ToListAsync();
            var byPath = imported.GroupBy(x => x.FilePath).ToDictionary(g => g.Key, g => g.First().Id);

            var replays = files.Select(f => new LocalReplayDto(
                f.Name, f.Length, new DateTimeOffset(f.LastWriteTimeUtc), byPath.TryGetValue(f.FullName, out var id) ? id : null));
            return Results.Ok(new { replaysDir = dir, exists = true, replays });
        });

        api.MapPost("/demos/import-local", async (ImportLocalRequest req, IOptions<ClipperOptions> options, AppDbContext db, ParseQueue queue) =>
        {
            var dir = options.Value.ReplaysDir;
            if (string.IsNullOrWhiteSpace(dir)) return Results.Problem("Clipper:ReplaysDir is not configured.", statusCode: 400);

            // Only bare file names from the configured folder — no path traversal.
            if (req.FileName != Path.GetFileName(req.FileName) || !req.FileName.EndsWith(".dem", StringComparison.OrdinalIgnoreCase))
                return Results.Problem("Invalid file name.", statusCode: 400);

            var path = Path.GetFullPath(Path.Combine(dir, req.FileName));
            if (!File.Exists(path)) return Results.NotFound();

            var existing = await db.Demos.FirstOrDefaultAsync(d => d.FilePath == path);
            if (existing is not null) return Results.Ok(ToSummary(existing, queue));

            var demo = new Demo
            {
                Id = Guid.NewGuid(),
                FileName = req.FileName,
                FilePath = path,
                Source = DemoSource.LocalReplay,
                SizeBytes = new FileInfo(path).Length,
                MatchId = long.TryParse(Path.GetFileNameWithoutExtension(req.FileName), out var m) ? m : null,
                Status = DemoStatus.Queued,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Demos.Add(demo);
            await db.SaveChangesAsync();
            queue.Enqueue(demo.Id);
            return Results.Accepted($"/api/demos/{demo.Id}", ToSummary(demo, queue));
        });

        // Download by match id: salts from the Game Coordinator, the replay from Valve, then parse (see MatchEndpoints).
        api.MapPost("/demos/from-match/{matchId:long}", MatchEndpoints.StartDownloadAsync);

        api.MapGet("/heroes", (HeroCatalog heroes) => heroes.All.Values.OrderBy(h => h.Name));
    }

    /// <summary>Streams the multipart body straight to disk instead of buffering 500 MB through IFormFile.</summary>
    private static async Task<IResult> UploadAsync(
        HttpRequest request,
        AppDbContext db,
        DemoStorage storage,
        ParseQueue queue,
        IOptions<ClipperOptions> options,
        CancellationToken ct)
    {
        var maxBytes = options.Value.MaxUploadBytes;
        var sizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = maxBytes + 1024 * 1024;

        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType) ||
            !contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            return Results.Problem("Expected multipart/form-data.", statusCode: 400);

        var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;
        if (string.IsNullOrEmpty(boundary)) return Results.Problem("Missing multipart boundary.", statusCode: 400);

        var reader = new MultipartReader(boundary, request.Body);
        while (await reader.ReadNextSectionAsync(ct) is { } section)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) ||
                !disposition.DispositionType.Equals("form-data") ||
                string.IsNullOrEmpty(disposition.FileName.Value ?? disposition.FileNameStar.Value))
                continue;

            var fileName = Path.GetFileName(disposition.FileNameStar.Value ?? disposition.FileName.Value!);
            if (!fileName.EndsWith(".dem", StringComparison.OrdinalIgnoreCase))
                return Results.Problem("Only .dem files are supported.", statusCode: 400);

            var id = Guid.NewGuid();
            long size;
            try
            {
                size = await storage.SaveAsync(id, section.Body, maxBytes, ct);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            var demo = new Demo
            {
                Id = id,
                FileName = fileName,
                FilePath = storage.PathFor(id),
                Source = DemoSource.Upload,
                SizeBytes = size,
                MatchId = long.TryParse(Path.GetFileNameWithoutExtension(fileName), out var m) ? m : null,
                Status = DemoStatus.Queued,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Demos.Add(demo);
            await db.SaveChangesAsync(ct);
            queue.Enqueue(demo.Id);
            return Results.Accepted($"/api/demos/{demo.Id}", ToSummary(demo, queue));
        }

        return Results.Problem("No file in upload.", statusCode: 400);
    }

    internal static DemoSummaryDto ToSummary(Demo d, ParseQueue queue) => new(
        d.Id, d.FileName, d.Source, d.SizeBytes, d.MatchId, d.Status,
        d.Status switch
        {
            DemoStatus.Done => 1,
            DemoStatus.Downloading or DemoStatus.Parsing or DemoStatus.Queued => queue.ProgressOf(d.Id) ?? 0,
            _ => null,
        },
        d.Error, d.Map, d.DurationSeconds, d.HighlightCount, d.CreatedAt, d.ParsedAt);
}
