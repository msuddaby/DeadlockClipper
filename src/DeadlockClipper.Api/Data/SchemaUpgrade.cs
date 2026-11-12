using Microsoft.EntityFrameworkCore;

namespace DeadlockClipper.Api.Data;

/// <summary>
/// The database was created with EnsureCreated (no migration history), which never alters an existing file.
/// This applies additive changes idempotently: missing tables are created, missing columns added.
/// </summary>
public static class SchemaUpgrade
{
    public static void Apply(AppDbContext db)
    {
        db.Database.EnsureCreated(); // fresh database: creates everything from the model

        if (!TableExists(db, "Renders"))
        {
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE "Renders" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_Renders" PRIMARY KEY,
                    "DemoId" TEXT NOT NULL,
                    "HighlightId" TEXT NOT NULL,
                    "Status" TEXT NOT NULL,
                    "Error" TEXT NULL,
                    "OutputPath" TEXT NULL,
                    "PlayerName" TEXT NOT NULL,
                    "HighlightType" TEXT NOT NULL,
                    "HeroId" INTEGER NOT NULL,
                    "GameTimeSeconds" REAL NOT NULL,
                    "MatchId" INTEGER NULL,
                    "CreatedAt" INTEGER NOT NULL,
                    "FinishedAt" INTEGER NULL
                );
                CREATE INDEX "IX_Renders_DemoId_HighlightId" ON "Renders" ("DemoId", "HighlightId");
                CREATE INDEX "IX_Renders_Status" ON "Renders" ("Status");
                """);
        }

        if (!TableExists(db, "Matches"))
        {
            db.Database.ExecuteSqlRaw("""
                CREATE TABLE "Matches" (
                    "MatchId" INTEGER NOT NULL CONSTRAINT "PK_Matches" PRIMARY KEY,
                    "PlayerSteamId" INTEGER NOT NULL,
                    "StartTime" INTEGER NOT NULL,
                    "DurationSeconds" INTEGER NOT NULL,
                    "HeroId" INTEGER NOT NULL,
                    "Kills" INTEGER NOT NULL,
                    "Deaths" INTEGER NOT NULL,
                    "Assists" INTEGER NOT NULL,
                    "NetWorth" INTEGER NOT NULL,
                    "MetadataStatus" TEXT NOT NULL,
                    "Error" TEXT NULL,
                    "Score" REAL NULL,
                    "HighlightsJson" TEXT NULL,
                    "ReplayValidThrough" INTEGER NULL,
                    "ScannedAt" INTEGER NULL
                );
                CREATE INDEX "IX_Matches_PlayerSteamId_StartTime" ON "Matches" ("PlayerSteamId", "StartTime");
                CREATE INDEX "IX_Matches_MetadataStatus" ON "Matches" ("MetadataStatus");
                """);
        }

        if (!ColumnExists(db, "Matches", "ClusterId"))
        {
            db.Database.ExecuteSqlRaw("""ALTER TABLE "Matches" ADD COLUMN "ClusterId" INTEGER NULL;""");
            db.Database.ExecuteSqlRaw("""ALTER TABLE "Matches" ADD COLUMN "ReplaySalt" INTEGER NULL;""");
        }
        if (!ColumnExists(db, "Matches", "MetadataSalt"))
            db.Database.ExecuteSqlRaw("""ALTER TABLE "Matches" ADD COLUMN "MetadataSalt" INTEGER NULL;""");

        if (!ColumnExists(db, "Matches", "ScoreVersion"))
            db.Database.ExecuteSqlRaw("""ALTER TABLE "Matches" ADD COLUMN "ScoreVersion" INTEGER NOT NULL DEFAULT 0;""");

        if (!ColumnExists(db, "Demos", "AutoMode"))
        {
            db.Database.ExecuteSqlRaw("""ALTER TABLE "Demos" ADD COLUMN "AutoMode" INTEGER NOT NULL DEFAULT 0;""");
            db.Database.ExecuteSqlRaw("""ALTER TABLE "Demos" ADD COLUMN "AutoRenderedAt" INTEGER NULL;""");
        }

        if (!ColumnExists(db, "Demos", "AnalysisVersion"))
            db.Database.ExecuteSqlRaw("""ALTER TABLE "Demos" ADD COLUMN "AnalysisVersion" INTEGER NOT NULL DEFAULT 0;""");
    }

    private static bool TableExists(AppDbContext db, string table) =>
        db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = {table}")
            .AsEnumerable().First() > 0;

    private static bool ColumnExists(AppDbContext db, string table, string column) =>
        db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM pragma_table_info({table}) WHERE name = {column}")
            .AsEnumerable().First() > 0;
}
