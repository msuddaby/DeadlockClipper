using Microsoft.EntityFrameworkCore;

namespace DeadlockClipper.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Demo> Demos => Set<Demo>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        var demo = b.Entity<Demo>();
        demo.Property(d => d.Source).HasConversion<string>();
        demo.Property(d => d.Status).HasConversion<string>();
        // SQLite can't order by DateTimeOffset natively; store as unix ms.
        demo.Property(d => d.CreatedAt).HasConversion(v => v.ToUnixTimeMilliseconds(), v => DateTimeOffset.FromUnixTimeMilliseconds(v));
        demo.Property(d => d.ParsedAt).HasConversion(
            v => v.HasValue ? v.Value.ToUnixTimeMilliseconds() : (long?)null,
            v => v.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(v.Value) : null);
        demo.HasIndex(d => d.FilePath);
        demo.HasIndex(d => d.CreatedAt);
    }
}
