using System.Text;
using Dante.Worker.Brain;
using Dante.Worker.Brain.Postgres;
using Npgsql;
using static Dante.Worker.Brain.Postgres.BrainMigrations;

namespace Dante.Tests;

// Disposable databases only. DANTE_BRAIN_TEST_CONNECTION points to a dedicated local test server.
public sealed class BrainPostgresTests
{
    [BrainPostgresFact]
    public async Task ScopeForeignKeysAndOptimisticRevisionsAreEnforcedTransactionally()
    {
        await using var db = await TestDatabase.CreateAsync();
        var a = await db.ScopeAsync(1); var b = await db.ScopeAsync(2);
        var record = await db.Storage.CommitAsync(Record(a, "original"), 0, []);
        Assert.Equal(record.Id, (await db.Storage.ReadAsync(a, record.Id))!.Id);
        Assert.Null(await db.Storage.ReadAsync(b, record.Id));
        await Assert.ThrowsAsync<BrainStorageException>(() => db.Storage.CommitAsync(record with { Content = "stale" }, 0, []));
        var other = await db.Storage.CommitAsync(Record(b, "other"), 0, []);
        var error = await Assert.ThrowsAsync<BrainStorageException>(() => db.Storage.CommitAsync(record with { Content = "illegal" }, 1,
            [new(record.Id, other.Id, "RELATES_TO")]));
        Assert.Equal(BrainStorageFailure.InvalidReference, error.Failure);
        Assert.Equal("original", (await db.Storage.ReadAsync(a, record.Id))!.Content);
        var missingProject = a with { IdProject = Guid.NewGuid() };
        await Assert.ThrowsAsync<BrainStorageException>(() => db.Storage.CommitAsync(Record(missingProject, "x"), 0, []));
        await db.Storage.RegisterScopeAsync(missingProject);
        var p = await db.Storage.CommitAsync(Record(missingProject, "project"), 0, []);
        Assert.Null(await db.Storage.ReadAsync(a, p.Id));
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async i =>
        {
            try { await db.Storage.CommitAsync(record with { Content = $"revision {i}" }, 1, []); return true; }
            catch (BrainStorageException e) when (e.Failure == BrainStorageFailure.Conflict) { return false; }
        }));
        Assert.Single(results, x => x);
        Assert.Equal(2, (await db.Storage.ReadAsync(a, record.Id))!.Revision);
        await Assert.ThrowsAsync<BrainStorageException>(() => db.Storage.DeleteAsync(a, record.Id, 1));
        await db.Storage.DeleteAsync(a, record.Id, 2);
        Assert.Null(await db.Storage.ReadAsync(a, record.Id));
    }

    [BrainPostgresFact]
    public async Task IdentityAndDataSurviveAdapterRestartAndIndexesAreDerived()
    {
        await using var db = await TestDatabase.CreateAsync();
        var scope = await db.ScopeAsync(12); var item = await db.Storage.CommitAsync(Record(scope, "incidente resolvido"), 0, []);
        Assert.Equal(1, (await db.Storage.HealthAsync()).PendingIndexes);
        await db.Storage.RebuildAsync();
        var health = await db.Storage.HealthAsync();
        Assert.True(health.CanonicalAvailable && health.LexicalReady); Assert.False(health.SemanticAvailable);
        Assert.Equal(0, health.PendingIndexes);
        await using var restart = new PostgresBrainStorage(db.Connection, db.Sources);
        var identity = await restart.ResolveIdentityAsync(12);
        Assert.Equal(scope.IdUser, identity.IdUser);
        Assert.Equal(item, await restart.ReadAsync(scope, item.Id));
        await db.SqlAsync("DROP SCHEMA brain_index CASCADE");
        Assert.True((await restart.HealthAsync()).CanonicalAvailable);
        Assert.False((await restart.HealthAsync()).LexicalReady);
        item = await restart.CommitAsync(item with { Content = "new canonical" }, 1, []);
        await restart.RebuildAsync();
        Assert.True((await restart.HealthAsync()).LexicalReady);
        Assert.Equal("new canonical", (await restart.ReadAsync(scope, item.Id))!.Content);
        await using var c = new NpgsqlConnection(db.Connection); await c.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT representation @@ plainto_tsquery('simple','canonical') FROM brain_index.lexical WHERE id_record=@id", c);
        cmd.Parameters.AddWithValue("id", item.Id);
        Assert.Equal(true, await cmd.ExecuteScalarAsync());
    }

    [BrainPostgresFact]
    public async Task MigrationsAreIdempotentConcurrentChecksumCheckedAndRollbackFailures()
    {
        await using var db = await TestDatabase.CreateAsync(migrate: false);
        await Task.WhenAll(db.Storage.MigrateAsync(), db.Storage.MigrateAsync());
        await db.Storage.MigrateAsync();
        Assert.Equal(1, await db.ScalarAsync("SELECT count(*) FROM brain_meta.migrations"));
        await using (var c = new NpgsqlConnection(db.Connection))
        {
            await c.OpenAsync();
            var failing = All.Concat(new[] { new BrainMigration(2, "CREATE TABLE brain_data.rollback_probe(id integer); SELECT 1/0;") }).ToArray();
            await Assert.ThrowsAsync<PostgresException>(() => ApplyAsync(c, failing, CancellationToken.None));
        }
        Assert.Equal(0, await db.ScalarAsync("SELECT count(*) FROM pg_tables WHERE schemaname='brain_data' AND tablename='rollback_probe'"));
        Assert.Equal(1, await db.ScalarAsync("SELECT count(*) FROM brain_meta.migrations"));
        await db.SqlAsync("UPDATE brain_meta.migrations SET checksum='tampered'");
        var error = await Assert.ThrowsAsync<BrainStorageException>(() => db.Storage.MigrateAsync());
        Assert.Equal(BrainStorageFailure.IncompatibleSchema, error.Failure);
        Assert.False((await db.Storage.HealthAsync()).SchemaCompatible);
        await db.SqlAsync("DELETE FROM brain_meta.migrations; INSERT INTO brain_meta.migrations(version,checksum) VALUES(999,'future')");
        await Assert.ThrowsAsync<BrainStorageException>(() => db.Storage.MigrateAsync());
        await Assert.ThrowsAsync<BrainStorageException>(() => db.Storage.ReadAsync(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid()));
    }

    [BrainPostgresFact]
    public async Task RuntimeCanWriteButCannotMigrateOrModifyLedger()
    {
        await using var db = await TestDatabase.CreateAsync();
        var scope = await db.ScopeAsync(21);
        await db.SqlAsync($"""
            CREATE ROLE {db.Role} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE;
            GRANT USAGE ON SCHEMA brain_data,brain_meta,brain_index TO {db.Role};
            GRANT SELECT,INSERT,UPDATE,DELETE ON ALL TABLES IN SCHEMA brain_data,brain_index TO {db.Role};
            GRANT SELECT ON brain_meta.migrations TO {db.Role};
            GRANT SELECT,INSERT,UPDATE,DELETE ON brain_meta.index_pending TO {db.Role};
            """);
        var settings = new NpgsqlConnectionStringBuilder(db.Connection) { Options = $"-c role={db.Role}" };
        await using var runtime = new PostgresBrainStorage(settings.ConnectionString, db.Sources, requireRestrictedRole: true);
        var item = await runtime.CommitAsync(Record(scope, "runtime"), 0, []);
        Assert.NotNull(await runtime.ReadAsync(scope, item.Id));
        await runtime.RebuildAsync();
        Assert.True((await runtime.HealthAsync()).LexicalReady);
        await using var unsafeRuntime = new PostgresBrainStorage(db.Connection, db.Sources, requireRestrictedRole: true);
        await Assert.ThrowsAsync<BrainStorageException>(() => unsafeRuntime.ReadAsync(scope, item.Id));
        await using var c = new NpgsqlConnection(settings.ConnectionString); await c.OpenAsync();
        await Assert.ThrowsAsync<PostgresException>(() => new NpgsqlCommand("CREATE TABLE brain_data.forbidden(id integer)", c).ExecuteNonQueryAsync());
        await Assert.ThrowsAsync<PostgresException>(() => new NpgsqlCommand("UPDATE brain_meta.migrations SET checksum='bad'", c).ExecuteNonQueryAsync());
        await Assert.ThrowsAsync<BrainStorageException>(() => runtime.MigrateAsync());
    }

    [BrainPostgresFact]
    public async Task OriginalSourcesAreValidatedAndBackupRestoresWithoutDerivedSchema()
    {
        await using var db = await TestDatabase.CreateAsync();
        var scope = await db.ScopeAsync(31);
        var orphan = await db.Sources.PublishAsync(new MemoryStream(Encoding.UTF8.GetBytes("órfão")));
        Assert.Equal(1, (await db.Storage.HealthAsync()).OrphanSources);
        var original = await db.Sources.PublishAsync(new MemoryStream(Encoding.UTF8.GetBytes("evidência original")));
        await Assert.ThrowsAsync<BrainStorageException>(() => db.Storage.CommitAsync(Record(scope, "missing") with { Kind = BrainRecordKind.SourceDocument, Original = new(Guid.NewGuid().ToString("N") + ".source", new string('a', 64), 1) }, 0, []));
        var item = await db.Storage.CommitAsync(Record(scope, "evidence") with { Kind = BrainRecordKind.SourceDocument, Original = original }, 0, []);
        await db.Storage.RebuildAsync();
        var tools = Environment.GetEnvironmentVariable("DANTE_BRAIN_TEST_TOOLS");
        var backup = Path.Combine(db.Root, "backup");
        await new BrainBackupService(db.Connection, db.Sources, tools).BackupAsync(backup);
        Assert.True(File.Exists(Path.Combine(backup, "manifest.json")));
        await using var restored = await TestDatabase.CreateAsync(migrate: false);
        await new BrainBackupService(restored.Connection, restored.Sources, tools).RestoreAsync(backup);
        Assert.Equal(item, await restored.Storage.ReadAsync(scope, item.Id));
        await restored.Sources.VerifyAsync(original);
        Assert.Equal(0, await restored.ScalarAsync("SELECT count(*) FROM pg_namespace WHERE nspname='brain_index'"));
        Assert.True((await restored.Storage.HealthAsync()).CanonicalAvailable);
        await restored.Storage.RebuildAsync();
        Assert.True((await restored.Storage.HealthAsync()).LexicalReady);
        await Assert.ThrowsAsync<ArgumentException>(() => new BrainBackupService(restored.Connection, restored.Sources, tools).RestoreAsync(backup));
        await File.WriteAllTextAsync(Path.Combine(db.Sources.Root, original.Reference), "corrompido");
        Assert.False((await db.Storage.HealthAsync()).SourcesIntact);
        var bad = Path.Combine(db.Root, "bad-backup");
        await Assert.ThrowsAsync<BrainStorageException>(() => new BrainBackupService(db.Connection, db.Sources, tools).BackupAsync(bad));
        Assert.False(File.Exists(Path.Combine(bad, "manifest.json")));
        await using var empty = await TestDatabase.CreateAsync(migrate: false);
        await File.AppendAllTextAsync(Path.Combine(backup, "brain.dump"), "tampered");
        await Assert.ThrowsAsync<BrainStorageException>(() => new BrainBackupService(empty.Connection, empty.Sources, tools).RestoreAsync(backup));
        Assert.Equal(0, await empty.ScalarAsync("SELECT count(*) FROM pg_namespace WHERE nspname='brain_data'"));
    }

    [BrainPostgresFact]
    public async Task MaintenanceLockWaitsForWritersAndRebuildNeverLosesConcurrentUpdates()
    {
        await using var db = await TestDatabase.CreateAsync(); var scope = await db.ScopeAsync(41);
        var item = await db.Storage.CommitAsync(Record(scope, "before"), 0, []);
        await using var c = new NpgsqlConnection(db.Connection); await c.OpenAsync();
        await ExecuteAsync(c, "SELECT pg_advisory_lock(@id)", CancellationToken.None, ("id", MaintenanceLock));
        var write = db.Storage.CommitAsync(item with { Content = "after" }, 1, []);
        var rebuild = db.Storage.RebuildAsync();
        await Task.Delay(100);
        Assert.False(write.IsCompleted); Assert.False(rebuild.IsCompleted);
        await ExecuteAsync(c, "SELECT pg_advisory_unlock(@id)", CancellationToken.None, ("id", MaintenanceLock));
        await Task.WhenAll(write, rebuild);
        var health = await db.Storage.HealthAsync();
        Assert.True(health.PendingIndexes == 1 || health.LexicalReady);
        await db.Storage.RebuildAsync();
        Assert.Equal(0, (await db.Storage.HealthAsync()).PendingIndexes);
        Assert.Equal("after", (await db.Storage.ReadAsync(scope, item.Id))!.Content);
    }

    private static BrainRecord Record(BrainScope s, string content) => new()
    { IdTenant = s.IdTenant, IdUser = s.IdUser, IdKnowledgeSpace = s.IdKnowledgeSpace, IdProject = s.IdProject, Kind = BrainRecordKind.KnowledgeItem, Content = content };

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly string admin;
        private readonly string name = "brain_test_" + Guid.NewGuid().ToString("N");
        public string Role => name + "_runtime";
        public string Root { get; } = Directory.CreateTempSubdirectory("brain-pg-test-").FullName;
        public string Connection { get; }
        public BrainSourceFiles Sources { get; }
        public PostgresBrainStorage Storage { get; }
        private TestDatabase()
        {
            admin = Environment.GetEnvironmentVariable("DANTE_BRAIN_TEST_CONNECTION")!;
            Connection = new NpgsqlConnectionStringBuilder(admin) { Database = name, Pooling = false }.ConnectionString;
            Sources = new BrainSourceFiles(Path.Combine(Root, "sources")); Storage = new(Connection, Sources);
        }
        public static async Task<TestDatabase> CreateAsync(bool migrate = true)
        {
            var db = new TestDatabase(); await using var c = new NpgsqlConnection(db.admin); await c.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE {db.name}", c).ExecuteNonQueryAsync();
            if (migrate) await db.Storage.MigrateAsync(); return db;
        }
        public async Task<BrainScope> ScopeAsync(long telegram)
        {
            var identity = await Storage.ResolveIdentityAsync(telegram);
            var scope = new BrainScope(identity.IdTenant, identity.IdUser, Guid.NewGuid()); await Storage.RegisterScopeAsync(scope); return scope;
        }
        public async Task SqlAsync(string sql)
        { await using var c = new NpgsqlConnection(Connection); await c.OpenAsync(); await new NpgsqlCommand(sql, c).ExecuteNonQueryAsync(); }
        public async Task<long> ScalarAsync(string sql)
        { await using var c = new NpgsqlConnection(Connection); await c.OpenAsync(); return (long)(await new NpgsqlCommand(sql, c).ExecuteScalarAsync())!; }
        public async ValueTask DisposeAsync()
        {
            await Storage.DisposeAsync(); NpgsqlConnection.ClearAllPools();
            await using var c = new NpgsqlConnection(admin); await c.OpenAsync();
            await new NpgsqlCommand($"DROP DATABASE {name} WITH (FORCE);DROP ROLE IF EXISTS {Role}", c).ExecuteNonQueryAsync();
            Directory.Delete(Root, true);
        }
    }
}

public sealed class BrainPostgresFactAttribute : FactAttribute
{
    public BrainPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DANTE_BRAIN_TEST_CONNECTION")))
            Skip = "Defina DANTE_BRAIN_TEST_CONNECTION em uma instância PostgreSQL 17 exclusiva de testes e instale pg_dump/pg_restore 17.";
    }
}
