namespace Dante.Worker.Brain;

// Physical persistence envelopes, not the Knowledge Core's semantic entities (#138).
public abstract record EntityBase
{
    public Guid Id { get; init; } = Guid.NewGuid();
}

public sealed record BrainScope(Guid IdTenant, Guid IdUser, Guid IdKnowledgeSpace, Guid? IdProject = null)
{
    public void Validate()
    {
        if (IdTenant == Guid.Empty || IdUser == Guid.Empty || IdKnowledgeSpace == Guid.Empty || IdProject == Guid.Empty)
            throw new ArgumentException("Escopo Brain ausente ou inválido.");
    }
}

public sealed record BrainIdentity(Guid IdTenant, Guid IdUser);
public enum BrainRecordKind { KnowledgeItem, SourceDocument, WorkingContext }

public sealed record BrainRecord : EntityBase
{
    public required Guid IdTenant { get; init; }
    public required Guid IdUser { get; init; }
    public required Guid IdKnowledgeSpace { get; init; }
    public Guid? IdProject { get; init; }
    public required BrainRecordKind Kind { get; init; }
    public required string Content { get; init; }
    public string DataJson { get; init; } = "{}";
    public string ProvenanceJson { get; init; } = "{}";
    public long Revision { get; init; }
    public SourceFile? Original { get; init; }
    public BrainScope Scope => new(IdTenant, IdUser, IdKnowledgeSpace, IdProject);
}

public sealed record BrainLink(Guid IdFrom, Guid IdTo, string Kind);
public sealed record SourceFile(string Reference, string Sha256, long Length);
public sealed record BrainHealth(bool CanonicalAvailable, bool SchemaCompatible, bool SourcesIntact,
    bool LexicalReady, bool SemanticAvailable, long PendingIndexes, string? Problem = null, long OrphanSources = 0);
public enum BrainStorageFailure { Unavailable, IncompatibleSchema, Integrity, Conflict, InvalidReference, PrivilegedRole }
public sealed class BrainStorageException(BrainStorageFailure failure) : Exception(failure switch
{
    BrainStorageFailure.Conflict => "Conflito de revisão no Brain.",
    BrainStorageFailure.IncompatibleSchema => "Schema Brain incompatível ou migration alterada.",
    BrainStorageFailure.Integrity => "Falha de integridade do Brain.",
    BrainStorageFailure.PrivilegedRole => "Conta Brain de runtime possui privilégios excessivos.",
    BrainStorageFailure.InvalidReference => "Referência ou escopo Brain inválido.",
    _ => "Armazenamento Brain indisponível."
})
{
    public BrainStorageFailure Failure { get; } = failure;
}

public interface IBrainStorage
{
    Task<BrainIdentity> ResolveIdentityAsync(long telegramUser, CancellationToken ct = default);
    Task RegisterScopeAsync(BrainScope scope, CancellationToken ct = default);
    Task<BrainRecord?> ReadAsync(BrainScope scope, Guid id, CancellationToken ct = default);
    Task<BrainRecord> CommitAsync(BrainRecord record, long expectedRevision,
        IReadOnlyList<BrainLink> links, CancellationToken ct = default);
    Task DeleteAsync(BrainScope scope, Guid id, long expectedRevision, CancellationToken ct = default);
    Task<BrainHealth> HealthAsync(CancellationToken ct = default);
}

public interface IBrainDerivedIndex
{
    Task RebuildAsync(CancellationToken ct = default);
}
