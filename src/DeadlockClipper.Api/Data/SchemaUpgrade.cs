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
