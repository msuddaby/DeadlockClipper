using DeadlockClipper.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DeadlockClipper.Api.Tests;

public sealed class SchemaUpgradeTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"clipper-test-{Guid.NewGuid():N}.db");

    private AppDbContext Open() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={_path}").Options);

    [Fact]
    public void MatchesKeyedByMatchIdAlone_AreRekeyedPerPlayer_KeepingRows()
    {
        // A database from before matches were per player: create the model, then swap Matches for the old shape.
        using (var db = Open())
        {
            db.Database.EnsureCreated();
            db.Database.ExecuteSqlRaw("""
                DROP TABLE "Matches";
                CREATE TABLE "Matches" (
                    "MatchId" INTEGER NOT NULL CONSTRAINT "PK_Matches" PRIMARY KEY,
                    "PlayerSteamId" INTEGER NOT NULL, "StartTime" INTEGER NOT NULL, "DurationSeconds" INTEGER NOT NULL,
                    "HeroId" INTEGER NOT NULL, "Kills" INTEGER NOT NULL, "Deaths" INTEGER NOT NULL, "Assists" INTEGER NOT NULL,
                    "NetWorth" INTEGER NOT NULL, "MetadataStatus" TEXT NOT NULL, "Error" TEXT NULL, "Score" REAL NULL,
                    "HighlightsJson" TEXT NULL, "ReplayValidThrough" INTEGER NULL, "ScannedAt" INTEGER NULL,
                    "ClusterId" INTEGER NULL, "ReplaySalt" INTEGER NULL, "MetadataSalt" INTEGER NULL,
                    "ScoreVersion" INTEGER NOT NULL DEFAULT 0);
                INSERT INTO "Matches" VALUES (10, 1, 0, 1800, 7, 5, 2, 3, 30000, 'Done', NULL, 12.5, '[]', NULL, NULL, 3, 44, 55, 4);
                """);
        }

        using (var db = Open()) SchemaUpgrade.Apply(db);

        using (var db = Open())
        {
            var row = db.Matches.Single();
            Assert.Equal((10, 1, 12.5, 44, 4), (row.MatchId, row.PlayerSteamId, row.Score!.Value, row.ReplaySalt!.Value, row.ScoreVersion));

            // The same match for a second player now fits alongside.
            db.Matches.Add(new ScannedMatch { MatchId = 10, PlayerSteamId = 2, MetadataStatus = MetadataStatus.Pending });
            db.SaveChanges();
            Assert.Equal(2, db.Matches.Count(m => m.MatchId == 10));
        }

        using (var db = Open()) SchemaUpgrade.Apply(db); // idempotent
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }
}
