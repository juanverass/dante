using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Dante.Worker.Brain.Postgres;

internal sealed record BrainMigration(int Version, string Sql)
{
    public string Checksum => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Sql)));
}

internal static class BrainMigrations
{
    internal const long MigrationLock = 16034001;
    internal const long MaintenanceLock = 16034002;
    internal static readonly BrainMigration[] All = Load();
    private static BrainMigration[] Load()
    {
        var assembly = typeof(BrainMigrations).Assembly;
        using var reader = new StreamReader(assembly.GetManifestResourceStream(
            "Dante.Worker.Brain.Postgres.Migrations.001_storage.sql")!);
        return [new(1, reader.ReadToEnd())];
    }

    internal static async Task ApplyAsync(NpgsqlConnection connection, IReadOnlyList<BrainMigration> migrations,
        CancellationToken ct)
    {
        if (connection.PostgreSqlVersion.Major != 17)
            throw new BrainStorageException(BrainStorageFailure.IncompatibleSchema);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await ExecuteAsync(connection, "SELECT pg_advisory_xact_lock(@id)", ct, ("id", MigrationLock));
        await ExecuteAsync(connection, "SELECT pg_advisory_xact_lock(@id)", ct, ("id", MaintenanceLock));
        await ExecuteAsync(connection, """
            CREATE SCHEMA IF NOT EXISTS brain_meta;
            CREATE TABLE IF NOT EXISTS brain_meta.migrations (
              version integer PRIMARY KEY, checksum text NOT NULL, applied_at timestamptz NOT NULL DEFAULT now());
            """, ct);
        var applied = await ReadLedgerAsync(connection, ct);
        if (applied.Any(entry => migrations.All(m => m.Version != entry.Key || m.Checksum != entry.Value)) ||
            applied.Keys.Order().Where((version, i) => version != i + 1).Any())
            throw new BrainStorageException(BrainStorageFailure.IncompatibleSchema);
        foreach (var migration in migrations.OrderBy(m => m.Version))
        {
            if (applied.ContainsKey(migration.Version)) continue;
            await ExecuteAsync(connection, migration.Sql, ct);
            await ExecuteAsync(connection, "INSERT INTO brain_meta.migrations(version,checksum) VALUES (@v,@hash)",
                ct, ("v", migration.Version), ("hash", migration.Checksum));
        }
        await tx.CommitAsync(ct);
    }

    internal static async Task ValidateAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        if (connection.PostgreSqlVersion.Major != 17)
            throw new BrainStorageException(BrainStorageFailure.IncompatibleSchema);
        var ledger = await ReadLedgerAsync(connection, ct);
        if (ledger.Count != All.Length || All.Any(m => !ledger.TryGetValue(m.Version, out var hash) || hash != m.Checksum))
            throw new BrainStorageException(BrainStorageFailure.IncompatibleSchema);
    }

    internal static async Task<Dictionary<int, string>> ReadLedgerAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT version,checksum FROM brain_meta.migrations ORDER BY version", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new Dictionary<int, string>();
        while (await reader.ReadAsync(ct)) result.Add(reader.GetInt32(0), reader.GetString(1));
        return result;
    }

    internal static async Task<int> ExecuteAsync(NpgsqlConnection c, string sql, CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, c);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(ct);
    }
}
