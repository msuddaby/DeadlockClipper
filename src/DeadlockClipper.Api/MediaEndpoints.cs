using System.Globalization;
using DeadlockClipper.Api.Data;
using DeadlockClipper.Api.Storage;
using DeadlockClipper.GameData;
using DeadlockClipper.Render;

namespace DeadlockClipper.Api;

/// <summary>Images for the UI: hero art from the local game install, and clip thumbnails.</summary>
public static class MediaEndpoints
{
    public static void MapMediaApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        // card = portrait, gloat = victory pose, logo = name wordmark (SVG). Read from the player's own install.
        api.MapGet("/heroes/{id:int}/art/{kind}", (int id, string kind, HeroAssets? assets, HttpContext http) =>
        {
            var image = kind switch
            {
                "card" => HeroImage.Card,
                "gloat" => HeroImage.CardGloat,
                "logo" => HeroImage.Logo,
                _ => (HeroImage?)null,
            };
            if (image == null || assets == null) return Results.NotFound();
            var path = assets.Get(id, image.Value);
            if (path == null) return Results.NotFound();
            http.Response.Headers.CacheControl = "public, max-age=86400";
            return Results.File(path, path.EndsWith(".svg", StringComparison.Ordinal) ? "image/svg+xml" : "image/png");
        });

        // A representative frame from the middle of the clip (past the intro card), extracted once and cached.
        api.MapGet("/renders/{id:guid}/thumb", async (Guid id, AppDbContext db, DemoStorage storage, IProcessRunner runner,
            HttpContext http, CancellationToken ct) =>
        {
            var r = await db.Renders.FindAsync([id], ct);
            if (r?.OutputPath is null || !File.Exists(r.OutputPath)) return Results.NotFound();

            var dir = Path.Combine(storage.DataDir, "thumbs");
            var thumb = Path.Combine(dir, $"{id:N}-v2.jpg");
            if (!File.Exists(thumb) || File.GetLastWriteTimeUtc(thumb) < File.GetLastWriteTimeUtc(r.OutputPath))
            {
                Directory.CreateDirectory(dir);
                var probe = await runner.RunAsync("ffprobe",
                    ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", r.OutputPath], ct: ct);
                var duration = double.TryParse(probe.StdOut.Trim(), CultureInfo.InvariantCulture, out var d) ? d : 10;
                var at = Math.Max(0, duration * 0.45).ToString("0.##", CultureInfo.InvariantCulture);
                var tmp = thumb + ".tmp.jpg";
                // "thumbnail" picks the most typical of ~3 s of frames, so it can't land on a fade between
                // compilation segments (a single frame at a fixed time came out black).
                var result = await runner.RunAsync("ffmpeg",
                    ["-v", "error", "-y", "-ss", at, "-t", "3", "-i", r.OutputPath,
                     "-vf", "thumbnail=90,scale=640:-2", "-frames:v", "1", "-q:v", "4", tmp], ct: ct);
                if (!result.Success || !File.Exists(tmp)) return Results.NotFound();
                File.Move(tmp, thumb, overwrite: true);
            }
            http.Response.Headers.CacheControl = "public, max-age=3600";
            return Results.File(thumb, "image/jpeg");
        });
    }
}
