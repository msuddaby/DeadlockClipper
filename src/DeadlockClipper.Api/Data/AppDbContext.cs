using Microsoft.EntityFrameworkCore;

namespace DeadlockClipper.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Demo> Demos => Set<Demo>();
    public DbSet<ClipRender> Renders => Set<ClipRender>();
    public DbSet<ScannedMatch> Matches => Set<ScannedMatch>();
    public DbSet<MatchRoster> Rosters => Set<MatchRoster>();
    public DbSet<KnownPlayerName> PlayerNames => Set<KnownPlayerName>();

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
        demo.Property(d => d.AutoRenderedAt).HasConversion(toMsNullable);
        demo.HasIndex(d => d.FilePath);
        demo.HasIndex(d => d.CreatedAt);

        var render = b.Entity<ClipRender>();
        render.ToTable("Renders");
        render.Property(r => r.Status).HasConversion<string>();
        render.Property(r => r.CreatedAt).HasConversion(toMs);
        render.Property(r => r.FinishedAt).HasConversion(toMsNullable);
        render.HasIndex(r => new { r.DemoId, r.HighlightId });
        render.HasIndex(r => r.Status);

        var match = b.Entity<ScannedMatch>();
        match.ToTable("Matches");
        // One row per (match, player): two tracked players in the same match each get their own scores.
        match.HasKey(m => new { m.MatchId, m.PlayerSteamId });
        match.Property(m => m.MatchId).ValueGeneratedNever();
        match.Property(m => m.MetadataStatus).HasConversion<string>();
        match.Property(m => m.StartTime).HasConversion(toMs);
        match.Property(m => m.ReplayValidThrough).HasConversion(toMsNullable);
        match.Property(m => m.ScannedAt).HasConversion(toMsNullable);
        match.HasIndex(m => new { m.PlayerSteamId, m.StartTime });
        match.HasIndex(m => m.MetadataStatus);

        var roster = b.Entity<MatchRoster>();
        roster.ToTable("Rosters");
        roster.HasKey(r => r.MatchId);
        roster.Property(r => r.MatchId).ValueGeneratedNever();
        roster.Property(r => r.WinningTeam).HasConversion<string>();
        roster.Property(r => r.FetchedAt).HasConversion(toMs);

        var name = b.Entity<KnownPlayerName>();
        name.ToTable("PlayerNames");
        name.HasKey(n => n.SteamId);
        name.Property(n => n.SteamId).ValueGeneratedNever();
        name.Property(n => n.UpdatedAt).HasConversion(toMs);
    }
}
