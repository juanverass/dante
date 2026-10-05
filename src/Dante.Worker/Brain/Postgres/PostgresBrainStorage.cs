using System.Text.Json;
using Npgsql;
using static Dante.Worker.Brain.Postgres.BrainMigrations;

namespace Dante.Worker.Brain.Postgres;

public sealed class PostgresBrainStorage : IBrainStorage, IBrainDerivedIndex, IAsyncDisposable
{
    private readonly NpgsqlDataSource dataSource;
    private readonly bool requireRestrictedRole;
    internal BrainSourceFiles Sources { get; }
    // Fixed local tenant, independent of usernames, paths and PostgreSQL identities.
    private static readonly Guid LocalTenant = new("ba160000-0000-4000-8000-000000000001");

    public PostgresBrainStorage(string connectionString, BrainSourceFiles sources, bool requireRestrictedRole = false)
    {
        try
        {
            var settings = new NpgsqlConnectionStringBuilder(connectionString)
            { IncludeErrorDetail = false, PersistSecurityInfo = false };
            dataSource = NpgsqlDataSource.Create(settings.ConnectionString);
        }
        catch (ArgumentException) { throw new BrainStorageException(BrainStorageFailure.Unavailable); }
        Sources = sources;
        this.requireRestrictedRole = requireRestrictedRole;
    }

    public async Task MigrateAsync(CancellationToken ct = default) => await SafeAsync(async () =>
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await ApplyAsync(c, All, ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await ExecuteAsync(c, "SELECT pg_advisory_xact_lock(@id)", ct, ("id", MaintenanceLock));
        await EnsureIndexAsync(c, ct);
        await tx.CommitAsync(ct);
        return true;
    });

    public async Task<BrainIdentity> ResolveIdentityAsync(long telegramUser, CancellationToken ct = default)
    {
        if (telegramUser <= 0) throw new ArgumentException("Identidade Telegram inválida.");
        return await WriteAsync(async c =>
        {
            await ExecuteAsync(c, "INSERT INTO brain_data.tenants(id) VALUES(@id) ON CONFLICT DO NOTHING", ct, ("id", LocalTenant));
            await ExecuteAsync(c, """
                INSERT INTO brain_data.users(id,id_tenant,telegram_user) VALUES(@id,@tenant,@telegram)
                ON CONFLICT(telegram_user) DO NOTHING
                """, ct, ("id", Guid.NewGuid()), ("tenant", LocalTenant), ("telegram", telegramUser));
            await using var cmd = new NpgsqlCommand("SELECT id_tenant,id FROM brain_data.users WHERE telegram_user=@telegram", c);
            cmd.Parameters.AddWithValue("telegram", telegramUser);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            return new BrainIdentity(reader.GetGuid(0), reader.GetGuid(1));
        }, ct);
    }

    public async Task RegisterScopeAsync(BrainScope scope, CancellationToken ct = default)
    {
        scope.Validate();
        await WriteAsync(async c =>
        {
            // Ownership of an existing ID must never be silently reassigned.
            await ExecuteAsync(c, """
                INSERT INTO brain_data.spaces(id,id_tenant,id_user) VALUES(@space,@tenant,@user)
                ON CONFLICT(id) DO NOTHING
                """, ct, ScopeParameters(scope));
            await using (var cmd = ScopedCommand(c, "SELECT count(*) FROM brain_data.spaces WHERE id=@space AND id_tenant=@tenant AND id_user=@user", scope))
                if ((long)(await cmd.ExecuteScalarAsync(ct))! != 1) throw new BrainStorageException(BrainStorageFailure.InvalidReference);
            if (scope.IdProject is not null)
            {
                await ExecuteAsync(c, """
                    INSERT INTO brain_data.projects(id,id_tenant,id_user,id_space) VALUES(@project,@tenant,@user,@space)
                    ON CONFLICT(id) DO NOTHING
                    """, ct, ScopeParameters(scope));
                await using var cmd = ScopedCommand(c, "SELECT count(*) FROM brain_data.projects WHERE id=@project AND id_tenant=@tenant AND id_user=@user AND id_space=@space", scope);
                if ((long)(await cmd.ExecuteScalarAsync(ct))! != 1) throw new BrainStorageException(BrainStorageFailure.InvalidReference);
            }
            return true;
        }, ct);
    }

    public async Task<BrainRecord?> ReadAsync(BrainScope scope, Guid id, CancellationToken ct = default)
    {
        scope.Validate();
        return await SafeAsync(async () =>
        {
            await using var c = await dataSource.OpenConnectionAsync(ct);
            await ValidateRuntimeAsync(c, ct);
            return await ReadRecordAsync(c, scope, id, ct);
        });
    }

    internal const string ScopeFilter = "id_tenant=@tenant AND id_user=@user AND id_space=@space AND scope_project=@scopeProject";
    private static async Task<BrainRecord?> ReadRecordAsync(NpgsqlConnection c, BrainScope scope, Guid id, CancellationToken ct)
    {
        await using var cmd = ScopedCommand(c, $"""
            SELECT kind,revision,content,data::text,provenance::text,source_reference,source_hash,source_length
            FROM brain_data.records WHERE id=@id AND {ScopeFilter}
            """, scope, ("id", id));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new BrainRecord
        {
            Id = id,
            IdTenant = scope.IdTenant,
            IdUser = scope.IdUser,
            IdKnowledgeSpace = scope.IdKnowledgeSpace,
            IdProject = scope.IdProject,
            Kind = (BrainRecordKind)reader.GetInt32(0),
            Revision = reader.GetInt64(1),
            Content = reader.GetString(2),
            DataJson = reader.GetString(3),
            ProvenanceJson = reader.GetString(4),
            Original = reader.IsDBNull(5) ? null : new(reader.GetString(5), reader.GetString(6), reader.GetInt64(7))
        };
    }

    public async Task<BrainRecord> CommitAsync(BrainRecord record, long expectedRevision,
        IReadOnlyList<BrainLink> links, CancellationToken ct = default)
    {
        record.Scope.Validate();
        if (record.Id == Guid.Empty || expectedRevision < 0 || !Enum.IsDefined(record.Kind) ||
            record.Content is null || links.Any(l => l.IdFrom != record.Id || l.IdTo == Guid.Empty || string.IsNullOrWhiteSpace(l.Kind)))
            throw new ArgumentException("Registro Brain inválido.");
        using var data = JsonDocument.Parse(record.DataJson);
        using var provenance = JsonDocument.Parse(record.ProvenanceJson);
        if (record.Original is not null && record.Kind != BrainRecordKind.SourceDocument)
            throw new ArgumentException("Original só pode pertencer a uma fonte.");
        return await WriteAsync(async c =>
        {
            if (record.Original is not null) await Sources.VerifyAsync(record.Original, ct);
            var revision = checked(expectedRevision + 1);
            var parameters = ScopeParameters(record.Scope).Concat(new (string, object?)[]
            {
                ("id",record.Id),("kind",(int)record.Kind),("revision",revision),("expected",expectedRevision),
                ("content",record.Content),("data",record.DataJson),("provenance",record.ProvenanceJson),
                ("ref",record.Original?.Reference),("hash",record.Original?.Sha256),("length",record.Original?.Length)
            }).ToArray();
            var changed = await ExecuteAsync(c, expectedRevision == 0 ? """
                INSERT INTO brain_data.records(id,id_tenant,id_user,id_space,id_project,kind,revision,content,data,provenance,source_reference,source_hash,source_length)
                VALUES(@id,@tenant,@user,@space,@project,@kind,@revision,@content,@data::jsonb,@provenance::jsonb,@ref,@hash,@length)
                ON CONFLICT(id) DO NOTHING
                """ : $"""
                UPDATE brain_data.records SET revision=@revision,content=@content,data=@data::jsonb,provenance=@provenance::jsonb,
                source_reference=@ref,source_hash=@hash,source_length=@length
                WHERE id=@id AND {ScopeFilter} AND revision=@expected AND kind=@kind
                """, ct, parameters);
            if (changed != 1) throw new BrainStorageException(BrainStorageFailure.Conflict);
            await ExecuteAsync(c, """
                INSERT INTO brain_data.revisions(id_record,revision,content,data,provenance,source_reference,source_hash,source_length)
                SELECT id,revision,content,data,provenance,source_reference,source_hash,source_length FROM brain_data.records WHERE id=@id;
                DELETE FROM brain_data.links WHERE id_from=@id;
                INSERT INTO brain_meta.index_pending(id_record,revision) VALUES(@id,@revision)
                ON CONFLICT(id_record) DO UPDATE SET revision=excluded.revision;
                """, ct, ("id", record.Id), ("revision", revision));
            await InvalidateIndexAsync(c, record.Id, ct);
            foreach (var link in links)
                await ExecuteAsync(c, """
                    INSERT INTO brain_data.links(id_tenant,id_user,id_space,scope_project,id_from,id_to,kind)
                    VALUES(@tenant,@user,@space,@scopeProject,@id,@to,@link)
                    """, ct, ScopeParameters(record.Scope).Concat(new (string, object?)[] { ("id", record.Id), ("to", link.IdTo), ("link", link.Kind) }).ToArray());
            return record with { Revision = revision };
        }, ct);
    }

    public async Task DeleteAsync(BrainScope scope, Guid id, long expectedRevision, CancellationToken ct = default)
    {
        scope.Validate();
        await WriteAsync(async c =>
        {
            if (await ExecuteAsync(c, $"DELETE FROM brain_data.records WHERE id=@id AND {ScopeFilter} AND revision=@revision",
                ct, ScopeParameters(scope).Concat(new (string, object?)[] { ("id", id), ("revision", expectedRevision) }).ToArray()) != 1)
                throw new BrainStorageException(BrainStorageFailure.Conflict);
            await InvalidateIndexAsync(c, id, ct);
            // Files remain immutable orphans until an explicit maintenance cleanup, never delete referenced bytes here.
            return true;
        }, ct);
    }

    public async Task RebuildAsync(CancellationToken ct = default) => await WriteAsync(async c =>
    {
        // Freeze writers, then replace projection transactionally: no partial generations or lost updates.
        if (!await IndexExistsAsync(c, ct)) await EnsureIndexAsync(c, ct);
        await ExecuteAsync(c, """
            DELETE FROM brain_index.lexical;
            INSERT INTO brain_index.lexical(id_record,revision,id_tenant,id_user,id_space,scope_project,representation)
            SELECT id,revision,id_tenant,id_user,id_space,scope_project,to_tsvector('simple',content) FROM brain_data.records;
            DELETE FROM brain_meta.index_pending;
            """, ct);
        return true;
    }, ct, exclusive: true);

    public async Task<BrainHealth> HealthAsync(CancellationToken ct = default)
    {
        try
        {
            return await SafeAsync(async () =>
            {
                await using var c = await dataSource.OpenConnectionAsync(ct);
                await ValidateRuntimeAsync(c, ct);
                var intact = true;
                var references = await ReferencedSourcesAsync(c, ct);
                var known = references.Select(source => source.Reference).ToHashSet(StringComparer.Ordinal);
                var orphans = Directory.EnumerateFiles(Sources.Root).LongCount(path => !known.Contains(Path.GetFileName(path)));
                foreach (var source in references)
                    try { await Sources.VerifyAsync(source, ct); } catch (BrainStorageException) { intact = false; }
                var lexicalExists = await IndexExistsAsync(c, ct);
                await using var cmd = new NpgsqlCommand(lexicalExists ? """
                    SELECT (SELECT count(*) FROM brain_meta.index_pending),
                    NOT EXISTS(SELECT 1 FROM brain_data.records r LEFT JOIN brain_index.lexical l ON l.id_record=r.id
                               WHERE l.id_record IS NULL OR l.revision <> r.revision),
                    EXISTS(SELECT 1 FROM pg_extension e JOIN pg_namespace n ON n.oid=e.extnamespace
                           WHERE e.extname='vector' AND n.nspname='brain_index')
                    """ : "SELECT (SELECT count(*) FROM brain_meta.index_pending),false,false", c);
                await using var reader = await cmd.ExecuteReaderAsync(ct); await reader.ReadAsync(ct);
                // An extension alone isn't an embedding generator or a ready semantic index.
                return new BrainHealth(true, true, intact, reader.GetBoolean(1), false, reader.GetInt64(0),
                    intact ? null : "Fontes ausentes ou corrompidas.", orphans);
            });
        }
        catch (BrainStorageException e)
        {
            return new(false, false, false, false, false, 0, e.Message);
        }
    }

    internal static async Task<List<SourceFile>> ReferencedSourcesAsync(NpgsqlConnection c, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT DISTINCT source_reference,source_hash,source_length FROM brain_data.revisions WHERE source_reference IS NOT NULL
            """, c);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var sources = new List<SourceFile>();
        while (await reader.ReadAsync(ct)) sources.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        return sources;
    }

    private async Task ValidateRuntimeAsync(NpgsqlConnection c, CancellationToken ct)
    {
        await ValidateAsync(c, ct);
        if (!requireRestrictedRole) return;
        await using var cmd = new NpgsqlCommand("""
            SELECT rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls
              OR has_schema_privilege(current_user,'brain_data','CREATE')
              OR has_schema_privilege(current_user,'brain_meta','CREATE')
              OR has_table_privilege(current_user,'brain_meta.migrations','UPDATE')
            FROM pg_roles WHERE rolname=current_user
            """, c);
        if ((bool)(await cmd.ExecuteScalarAsync(ct))!)
            throw new BrainStorageException(BrainStorageFailure.PrivilegedRole);
    }

    private static async Task<bool> IndexExistsAsync(NpgsqlConnection c, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT to_regclass('brain_index.lexical') IS NOT NULL", c);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }
    private static async Task InvalidateIndexAsync(NpgsqlConnection c, Guid id, CancellationToken ct)
    {
        if (await IndexExistsAsync(c, ct))
            await ExecuteAsync(c, "DELETE FROM brain_index.lexical WHERE id_record=@id", ct, ("id", id));
    }
    private static async Task EnsureIndexAsync(NpgsqlConnection c, CancellationToken ct)
    {
        using var reader = new StreamReader(typeof(PostgresBrainStorage).Assembly.GetManifestResourceStream(
            "Dante.Worker.Brain.Postgres.Migrations.lexical.sql")!);
        await ExecuteAsync(c, await reader.ReadToEndAsync(ct), ct);
    }

    private async Task<T> WriteAsync<T>(Func<NpgsqlConnection, Task<T>> action, CancellationToken ct, bool exclusive = false) =>
        await SafeAsync(async () =>
        {
            await using var c = await dataSource.OpenConnectionAsync(ct);
            await using var tx = await c.BeginTransactionAsync(ct);
            await ExecuteAsync(c, exclusive ? "SELECT pg_advisory_xact_lock(@id)" : "SELECT pg_advisory_xact_lock_shared(@id)", ct, ("id", MaintenanceLock));
            await ValidateRuntimeAsync(c, ct);
            var result = await action(c);
            await tx.CommitAsync(ct);
            return result;
        });

    internal static async Task<T> SafeAsync<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (PostgresException e) when (e.SqlState is "23503" or "23514" or "23505")
        { throw new BrainStorageException(BrainStorageFailure.InvalidReference); }
        catch (PostgresException e) when (e.SqlState is "42P01" or "3F000")
        { throw new BrainStorageException(BrainStorageFailure.IncompatibleSchema); }
        catch (NpgsqlException) { throw new BrainStorageException(BrainStorageFailure.Unavailable); }
    }

    internal static (string, object?)[] ScopeParameters(BrainScope s) =>
        [("tenant", s.IdTenant), ("user", s.IdUser), ("space", s.IdKnowledgeSpace), ("project", s.IdProject), ("scopeProject", s.IdProject ?? Guid.Empty)];
    internal static NpgsqlCommand ScopedCommand(NpgsqlConnection c, string sql, BrainScope scope, params (string, object?)[] extra)
    {
        var cmd = new NpgsqlCommand(sql, c);
        foreach (var (name, value) in ScopeParameters(scope).Concat(extra))
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
    public ValueTask DisposeAsync() => dataSource.DisposeAsync();
}
