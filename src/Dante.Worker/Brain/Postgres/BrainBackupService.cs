using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using static Dante.Worker.Brain.Postgres.BrainMigrations;

namespace Dante.Worker.Brain.Postgres;

public sealed record BrainBackupManifest(int FormatVersion, Guid IdBackup, DateTimeOffset CreatedAt,
    int ServerMajor, string DumpSha256, Dictionary<int, string> Migrations,
    Dictionary<string, long> Counts, IReadOnlyList<SourceFile> Sources);

// Offline maintenance: this connection is privileged and is never registered in Worker DI.
public sealed class BrainBackupService(string connectionString, BrainSourceFiles sources, string? toolsDirectory = null)
{
    private static readonly string[] CanonicalTables = ["tenants", "users", "spaces", "projects", "records", "revisions", "links"];

    public async Task BackupAsync(string destination, CancellationToken ct = default) => await PostgresBrainStorage.SafeAsync(async () =>
    {
        if (!Path.IsPathFullyQualified(destination) || Directory.Exists(destination) || File.Exists(destination))
            throw new ArgumentException("Backup exige diretório absoluto novo.");
        ValidateBackupPath(destination);
        Directory.CreateDirectory(destination);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await using var c = new NpgsqlConnection(connectionString); await c.OpenAsync(ct);
        await ExecuteAsync(c, "SELECT pg_advisory_lock(@id)", ct, ("id", MaintenanceLock));
        try
        {
            await ValidateAsync(c, ct);
            var refs = await PostgresBrainStorage.ReferencedSourcesAsync(c, ct);
            foreach (var source in refs) await sources.VerifyAsync(source, ct);
            var serverMajor = await ServerMajorAsync(c, ct);
            await CheckToolAsync("pg_dump", serverMajor, ct);
            var dump = Path.Combine(destination, "brain.dump");
            await RunToolAsync("pg_dump", ["--format=custom", "--schema=brain_data", "--schema=brain_meta", "--no-acl", "--file", dump], ct);
            var sourceDir = Directory.CreateDirectory(Path.Combine(destination, "sources")).FullName;
            var copy = new BrainSourceFiles(sourceDir);
            foreach (var source in refs)
            {
                File.Copy(sources.Resolve(source.Reference), copy.Resolve(source.Reference), false);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(copy.Resolve(source.Reference), UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await copy.VerifyAsync(source, ct);
            }
            var manifest = new BrainBackupManifest(1, Guid.NewGuid(), DateTimeOffset.UtcNow, serverMajor,
                await HashAsync(dump, ct), await ReadLedgerAsync(c, ct), await CountsAsync(c, ct), refs);
            // Manifest is the completion marker; never produced for failed or partial backups.
            await File.WriteAllTextAsync(Path.Combine(destination, "manifest.json"), JsonSerializer.Serialize(manifest), ct);
            return true;
        }
        finally { await ExecuteAsync(c, "SELECT pg_advisory_unlock(@id)", CancellationToken.None, ("id", MaintenanceLock)); }
    });

    public async Task RestoreAsync(string backup, CancellationToken ct = default) => await PostgresBrainStorage.SafeAsync(async () =>
    {
        ValidateBackupPath(backup);
        var manifest = JsonSerializer.Deserialize<BrainBackupManifest>(await File.ReadAllTextAsync(Path.Combine(backup, "manifest.json"), ct))
            ?? throw new BrainStorageException(BrainStorageFailure.Integrity);
        if (manifest.FormatVersion != 1 || manifest.Migrations.Count != All.Length ||
            All.Any(m => !manifest.Migrations.TryGetValue(m.Version, out var hash) || hash != m.Checksum) ||
            manifest.DumpSha256 != await HashAsync(Path.Combine(backup, "brain.dump"), ct))
            throw new BrainStorageException(BrainStorageFailure.Integrity);
        var originals = new BrainSourceFiles(Path.Combine(backup, "sources"));
        foreach (var source in manifest.Sources) await originals.VerifyAsync(source, ct);
        if (Directory.EnumerateFileSystemEntries(sources.Root).Any()) throw new ArgumentException("Restore exige raiz de fontes vazia.");
        await using var c = new NpgsqlConnection(connectionString); await c.OpenAsync(ct);
        await ExecuteAsync(c, "SELECT pg_advisory_lock(@id)", ct, ("id", MaintenanceLock));
        try
        {
            await using (var cmd = new NpgsqlCommand("""
                SELECT (SELECT count(*) FROM pg_namespace
                        WHERE nspname NOT IN ('pg_catalog','information_schema','public') AND nspname NOT LIKE 'pg_%')
                     + (SELECT count(*) FROM pg_class r JOIN pg_namespace n ON n.oid=r.relnamespace
                        WHERE n.nspname='public' AND r.relkind IN ('r','p','v','m','S','f'))
                """, c))
                if ((long)(await cmd.ExecuteScalarAsync(ct))! != 0) throw new ArgumentException("Restore exige banco vazio separado.");
            if (await ServerMajorAsync(c, ct) != manifest.ServerMajor) throw new BrainStorageException(BrainStorageFailure.IncompatibleSchema);
            await CheckToolAsync("pg_restore", manifest.ServerMajor, ct);
            await RunToolAsync("pg_restore", ["--exit-on-error", "--single-transaction", "--no-owner", "--no-acl", "--dbname", new NpgsqlConnectionStringBuilder(connectionString).Database!, Path.Combine(backup, "brain.dump")], ct);
            await ValidateAsync(c, ct);
            var restoredRefs = await PostgresBrainStorage.ReferencedSourcesAsync(c, ct);
            if (!restoredRefs.OrderBy(s => s.Reference).SequenceEqual(manifest.Sources.OrderBy(s => s.Reference)) ||
                !(await CountsAsync(c, ct)).OrderBy(x => x.Key).SequenceEqual(manifest.Counts.OrderBy(x => x.Key)))
                throw new BrainStorageException(BrainStorageFailure.Integrity);
            foreach (var source in manifest.Sources)
            {
                File.Copy(originals.Resolve(source.Reference), sources.Resolve(source.Reference), false);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(sources.Resolve(source.Reference), UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await sources.VerifyAsync(source, ct);
            }
            // Rebuild/grants remain explicit; restore can succeed without pgvector or brain_index.
            return true;
        }
        finally { await ExecuteAsync(c, "SELECT pg_advisory_unlock(@id)", CancellationToken.None, ("id", MaintenanceLock)); }
    });

    private static void ValidateBackupPath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Backup exige caminho absoluto.");
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if (directory.LinkTarget is not null) throw new BrainStorageException(BrainStorageFailure.Integrity);
        foreach (var name in new[] { "brain.dump", "manifest.json" })
            if (new FileInfo(Path.Combine(path, name)).LinkTarget is not null) throw new BrainStorageException(BrainStorageFailure.Integrity);
    }

    private static async Task<int> ServerMajorAsync(NpgsqlConnection c, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SHOW server_version_num", c);
        var major = int.Parse((string)(await cmd.ExecuteScalarAsync(ct))!) / 10000;
        if (major != 17) throw new BrainStorageException(BrainStorageFailure.IncompatibleSchema);
        return major;
    }
    private static async Task<Dictionary<string, long>> CountsAsync(NpgsqlConnection c, CancellationToken ct)
    {
        var result = new Dictionary<string, long>();
        foreach (var table in CanonicalTables)
        {
            await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM brain_data.{table}", c);
            result.Add(table, (long)(await cmd.ExecuteScalarAsync(ct))!);
        }
        return result;
    }
    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
    }
    private async Task CheckToolAsync(string tool, int major, CancellationToken ct)
    {
        var version = await RunToolAsync(tool, ["--version"], ct);
        if (!version.Contains($"(PostgreSQL) {major}.", StringComparison.Ordinal))
            throw new BrainStorageException(BrainStorageFailure.IncompatibleSchema);
    }
    private async Task<string> RunToolAsync(string tool, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        if (toolsDirectory is not null && !Path.IsPathFullyQualified(toolsDirectory))
            throw new ArgumentException("Diretório dos clientes PostgreSQL deve ser absoluto.");
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        var info = new ProcessStartInfo
        {
            FileName = toolsDirectory is null ? tool : Path.Combine(toolsDirectory, tool),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        // No inherited provider/CLI credentials; the DB password is never a process argument.
        info.Environment.Clear();
        foreach (var name in new[] { "PATH", "SYSTEMROOT", "LD_LIBRARY_PATH" })
            if (Environment.GetEnvironmentVariable(name) is { } value) info.Environment[name] = value;
        info.Environment["PGHOST"] = settings.Host;
        info.Environment["PGPORT"] = settings.Port.ToString();
        info.Environment["PGUSER"] = settings.Username;
        info.Environment["PGDATABASE"] = settings.Database;
        info.Environment["PGPASSWORD"] = settings.Password;
        info.Environment["PGSSLMODE"] = settings.SslMode.ToString().ToLowerInvariant();
        info.Environment["PGCONNECT_TIMEOUT"] = "10";
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(info) ?? throw new BrainStorageException(BrainStorageFailure.Unavailable);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                throw;
            }
            await stderr; // Provider messages can contain content/secrets; never propagate them.
            if (process.ExitCode != 0) throw new BrainStorageException(BrainStorageFailure.Unavailable);
            return await stdout;
        }
        catch (System.ComponentModel.Win32Exception) { throw new BrainStorageException(BrainStorageFailure.Unavailable); }
    }
}
