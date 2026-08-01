using Microsoft.EntityFrameworkCore;

namespace RainBird.Server.Data;

/// <summary>
/// Brings an existing database up to date without destroying it.
///
/// The schema is created with <c>EnsureCreated</c>, which is right for a
/// single-file local app but does not alter an existing database when the model
/// changes. Recreating instead is not an option: controller passwords are encrypted
/// at rest and cannot be recovered from the app, so dropping the database means the
/// user has to re-enter credentials for hardware that was working fine.
///
/// Each step is idempotent and additive. Anything more elaborate than adding columns
/// and tables should move to EF Core migrations.
/// </summary>
public static class SchemaUpgrader
{
    public static async Task UpgradeAsync(AppDbContext db, ILogger logger, CancellationToken ct = default)
    {
        if (!await db.Database.CanConnectAsync(ct)) return;

        await AddColumnIfMissingAsync(db, logger, "Controllers", "UseHttps", "INTEGER NOT NULL DEFAULT 0", ct);
        await AddAutoDisabledAsync(db, logger, ct);
        await AddPlanTablesAsync(db, logger, ct);
    }

    /// <summary>
    /// Adds the flag that distinguishes a zone switched off because its station went
    /// missing from one the user switched off deliberately.
    ///
    /// Existing disabled zones are marked as automatic. Before this column there was
    /// no such distinction, and the only code path that disabled a zone was the
    /// automatic one — so that is the truthful reading. The cost of being wrong is
    /// that a zone someone deliberately disabled comes back on and has to be switched
    /// off again; the cost of the opposite default is a zone stuck invisible forever.
    /// </summary>
    private static async Task AddAutoDisabledAsync(AppDbContext db, ILogger logger, CancellationToken ct)
    {
        if (!await TableExistsAsync(db, "Zones", ct)) return;
        if (await ColumnExistsAsync(db, "Zones", "AutoDisabled", ct)) return;

        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Zones\" ADD COLUMN \"AutoDisabled\" INTEGER NOT NULL DEFAULT 0;", ct);

        var reconciled = await db.Database.ExecuteSqlRawAsync(
            "UPDATE \"Zones\" SET \"AutoDisabled\" = 1 WHERE \"Enabled\" = 0;", ct);

        logger.LogInformation(
            "Schema upgrade: added Zones.AutoDisabled and marked {Count} already-disabled zones as automatic",
            reconciled);
    }

    /// <summary>
    /// Watering plans arrived after the first release, so a database created before
    /// them has none of their tables and EnsureCreated will not add them.
    /// </summary>
    private static async Task AddPlanTablesAsync(AppDbContext db, ILogger logger, CancellationToken ct)
    {
        if (await TableExistsAsync(db, "WateringPlans", ct)) return;

        string[] statements =
        [
            """
            CREATE TABLE "WateringPlans" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_WateringPlans" PRIMARY KEY AUTOINCREMENT,
                "ControllerId" INTEGER NOT NULL,
                "Name" TEXT NOT NULL DEFAULT '',
                "Description" TEXT NOT NULL DEFAULT '',
                "Enabled" INTEGER NOT NULL DEFAULT 1,
                "Frequency" INTEGER NOT NULL DEFAULT 0,
                "DaysOfWeek" TEXT NOT NULL DEFAULT '0101010',
                "IntervalDays" INTEGER NOT NULL DEFAULT 2,
                "IntervalAnchor" TEXT NULL,
                "StartTimes" TEXT NOT NULL DEFAULT '360',
                "LatestStartMinute" INTEGER NULL,
                "SeasonalAdjustPercent" INTEGER NOT NULL DEFAULT 100,
                "CycleSoakEnabled" INTEGER NOT NULL DEFAULT 0,
                "Cycles" INTEGER NOT NULL DEFAULT 2,
                "SoakMinutes" INTEGER NOT NULL DEFAULT 15,
                "WeatherSkipEnabled" INTEGER NOT NULL DEFAULT 1,
                "SortOrder" INTEGER NOT NULL DEFAULT 0,
                "CreatedUtc" INTEGER NOT NULL DEFAULT 0
            )
            """,
            """
            CREATE TABLE "PlanZones" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_PlanZones" PRIMARY KEY AUTOINCREMENT,
                "WateringPlanId" INTEGER NOT NULL,
                "StationNumber" INTEGER NOT NULL,
                "Minutes" INTEGER NOT NULL DEFAULT 0,
                "SortOrder" INTEGER NOT NULL DEFAULT 0,
                CONSTRAINT "FK_PlanZones_WateringPlans" FOREIGN KEY ("WateringPlanId")
                    REFERENCES "WateringPlans" ("Id") ON DELETE CASCADE
            )
            """,
            """
            CREATE TABLE "PlanRuns" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_PlanRuns" PRIMARY KEY AUTOINCREMENT,
                "ControllerId" INTEGER NOT NULL,
                "WateringPlanId" INTEGER NOT NULL,
                "PlanName" TEXT NOT NULL DEFAULT '',
                "ScheduledDate" TEXT NOT NULL DEFAULT '0001-01-01',
                "ScheduledStartMinute" INTEGER NOT NULL DEFAULT 0,
                "StartedUtc" INTEGER NOT NULL DEFAULT 0,
                "EndedUtc" INTEGER NULL,
                "Status" INTEGER NOT NULL DEFAULT 0,
                "Detail" TEXT NULL,
                "StepIndex" INTEGER NOT NULL DEFAULT 0
            )
            """,
            """
            CREATE TABLE "PlanRunSteps" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_PlanRunSteps" PRIMARY KEY AUTOINCREMENT,
                "PlanRunId" INTEGER NOT NULL,
                "Ordinal" INTEGER NOT NULL DEFAULT 0,
                "StationNumber" INTEGER NOT NULL DEFAULT 0,
                "Cycle" INTEGER NOT NULL DEFAULT 1,
                "Minutes" INTEGER NOT NULL DEFAULT 0,
                "StartedUtc" INTEGER NULL,
                "EndedUtc" INTEGER NULL,
                "Status" INTEGER NOT NULL DEFAULT 0,
                CONSTRAINT "FK_PlanRunSteps_PlanRuns" FOREIGN KEY ("PlanRunId")
                    REFERENCES "PlanRuns" ("Id") ON DELETE CASCADE
            )
            """,
            """
            CREATE UNIQUE INDEX "IX_PlanRuns_Pass"
                ON "PlanRuns" ("WateringPlanId", "ScheduledDate", "ScheduledStartMinute")
            """,
            """
            CREATE INDEX "IX_PlanRuns_Controller" ON "PlanRuns" ("ControllerId", "StartedUtc")
            """,
            """
            CREATE INDEX "IX_WateringPlans_Controller" ON "WateringPlans" ("ControllerId")
            """,
        ];

        foreach (var statement in statements)
            await db.Database.ExecuteSqlRawAsync(statement, ct);

        logger.LogInformation("Schema upgrade: added the watering plan tables");
    }

    private static async Task AddColumnIfMissingAsync(
        AppDbContext db, ILogger logger, string table, string column, string definition, CancellationToken ct)
    {
        if (!await TableExistsAsync(db, table, ct)) return;
        if (await ColumnExistsAsync(db, table, column, ct)) return;

        // Identifiers here are compile-time constants from this file, never user input.
        await db.Database.ExecuteSqlRawAsync($"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition};", ct);
        logger.LogInformation("Schema upgrade: added {Table}.{Column}", table, column);
    }

    private static async Task<bool> TableExistsAsync(AppDbContext db, string table, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";

        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = table;
        command.Parameters.Add(parameter);

        await db.Database.OpenConnectionAsync(ct);
        var result = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result) > 0;
    }

    private static async Task<bool> ColumnExistsAsync(
        AppDbContext db, string table, string column, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";

        await db.Database.OpenConnectionAsync(ct);
        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
