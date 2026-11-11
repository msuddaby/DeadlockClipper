using Microsoft.EntityFrameworkCore;

namespace DeadlockClipper.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Demo> Demos => Set<Demo>();
    public DbSet<ClipRender> Renders => Set<ClipRender>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // SQLite can't order by DateTimeOffset natively; store as unix ms.
        var toMs = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, long>(
            v => v.ToUnixTimeMilliseconds(), v => DateTimeOffset.FromUnixTimeMilliseconds(v));
        var toMsNullable = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset?, long?>(
            v => v.HasValue ? v.Value.ToUnixTimeMilliseconds() : null,
            v => v.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(v.Value) : null);

        var demo = b.Entity<Demo>();
        demo.Property(d => d.Source).HasConversion<string>();
        demo.Property(d => d.Status).HasConversion<string>();
        demo.Property(d => d.CreatedAt).HasConversion(toMs);
        demo.Property(d => d.ParsedAt).HasConversion(toMsNullable);
        demo.HasIndex(d => d.FilePath);
        demo.HasIndex(d => d.CreatedAt);

        var render = b.Entity<ClipRender>();
        render.ToTable("Renders");
        render.Property(r => r.Status).HasConversion<string>();
        render.Property(r => r.CreatedAt).HasConversion(toMs);
        render.Property(r => r.FinishedAt).HasConversion(toMsNullable);
        render.HasIndex(r => new { r.DemoId, r.HighlightId });
        render.HasIndex(r => r.Status);
    }
}
