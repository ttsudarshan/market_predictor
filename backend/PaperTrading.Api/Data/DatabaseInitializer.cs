using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

namespace PaperTrading.Api.Data;

public static class DatabaseInitializer
{
    /// <summary>The first migration, which matches the schema of databases from before migrations existed.</summary>
    private const string BaselineMigration = "20261102014827_InitialCreate";

    /// <summary>Brings the database up to date, keeping existing accounts and saving a backup first.</summary>
    public static void Migrate(AppDbContext db, string dataDirectory, ILogger log)
    {
        var connection = db.Database.GetDbConnection();
        db.Database.OpenConnection();
        try
        {
            var hasTables = Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Users'") > 0;
            var hasHistory = Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory'") > 0;

            // A database made before migrations has the first migration's tables but no record of it.
            if (hasTables && !hasHistory)
            {
                Execute(connection, """
                    CREATE TABLE "__EFMigrationsHistory" ("MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY, "ProductVersion" TEXT NOT NULL);
                    """);
                Execute(connection, $"""
                    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES ('{BaselineMigration}', '8.0.33');
                    """);
                log.LogInformation("Marked the existing database as starting from {Migration}", BaselineMigration);
            }

            if (hasTables && db.Database.GetPendingMigrations().Any())
            {
                var backup = Path.Combine(dataDirectory, $"papertrading.backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");
                Execute(connection, $"VACUUM INTO '{backup.Replace("'", "''")}'");
                log.LogInformation("Saved a backup before upgrading the database: {Backup}", backup);
            }
        }
        finally
        {
            db.Database.CloseConnection();
        }

        db.Database.Migrate();
    }

    private static long Scalar(System.Data.Common.DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private static void Execute(System.Data.Common.DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
