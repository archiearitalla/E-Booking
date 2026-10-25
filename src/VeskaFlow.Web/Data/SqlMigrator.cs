using System.Reflection;
using Npgsql;

namespace VeskaFlow.Web.Data;

/// <summary>
/// Applies embedded Migrations/*.sql files in name order, once each, inside a transaction.
/// A PostgreSQL advisory lock prevents two app instances migrating at the same time.
/// Migrations are additive by policy: never add DROP/TRUNCATE/destructive ALTER without a reviewed plan.
/// </summary>
public static class SqlMigrator
{
    private const long AdvisoryLockKey = 727_274;

    public static async Task ApplyAsync(string connectionString, ILogger logger, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        await using (var create = new NpgsqlCommand(
            "CREATE TABLE IF NOT EXISTS schema_migrations (name text PRIMARY KEY, applied_at_utc timestamptz NOT NULL DEFAULT now())", conn))
            await create.ExecuteNonQueryAsync(ct);

        await using (var lockCmd = new NpgsqlCommand($"SELECT pg_advisory_lock({AdvisoryLockKey})", conn))
            await lockCmd.ExecuteNonQueryAsync(ct);

        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var scripts = assembly.GetManifestResourceNames()
                .Where(n => n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                .Select(n => (Resource: n, Name: ScriptName(n)))
                .OrderBy(x => x.Name, StringComparer.Ordinal)
                .ToList();

            foreach (var (resource, name) in scripts)
            {
                await using (var exists = new NpgsqlCommand("SELECT 1 FROM schema_migrations WHERE name = @n", conn))
                {
                    exists.Parameters.AddWithValue("n", name);
                    if (await exists.ExecuteScalarAsync(ct) is not null) continue;
                }

                string sql;
                await using (var stream = assembly.GetManifestResourceStream(resource)!)
                using (var reader = new StreamReader(stream))
                    sql = await reader.ReadToEndAsync(ct);

                await using var tx = await conn.BeginTransactionAsync(ct);
                await using (var run = new NpgsqlCommand(sql, conn, tx))
                    await run.ExecuteNonQueryAsync(ct);
                await using (var record = new NpgsqlCommand("INSERT INTO schema_migrations (name) VALUES (@n)", conn, tx))
                {
                    record.Parameters.AddWithValue("n", name);
                    await record.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
                logger.LogInformation("Applied migration {Migration}", name);
            }
        }
        finally
        {
            await using var unlock = new NpgsqlCommand($"SELECT pg_advisory_unlock({AdvisoryLockKey})", conn);
            await unlock.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    // "VeskaFlow.Web.Data.Migrations.001_init.sql" -> "001_init.sql"
    private static string ScriptName(string resource)
    {
        var marker = ".Migrations.";
        var i = resource.IndexOf(marker, StringComparison.Ordinal);
        return i < 0 ? resource : resource[(i + marker.Length)..];
    }
}
