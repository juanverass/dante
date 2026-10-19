namespace Dante.Infrastructure.Contextos;

// Adapter legado movido do Worker na #167: o nome em inglês fica até a migração explícita (AD-38).
// Links são os aliases vinculados diretamente a este repositório (AD-58); catálogos antigos não têm o campo.
public sealed record RepositoryDefinition(string Alias, string Path, string? GitHub,
    IReadOnlyList<RepositoryEnvironmentEntry>? Environment = null, IReadOnlyList<string>? Links = null);

public sealed record RepositoryEnvironmentEntry(string Key, string? LiteralValue, string? HostVariable);
