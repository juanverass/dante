namespace Dante.Application.Contextos;

// Legado movido do Worker na #166: o nome em inglês fica até a migração explícita (AD-38).
public enum JobExecutionMode { General, Repository }

public sealed record JobExecutionContext(JobExecutionMode Mode, string WorkingDirectory, string? RepositoryAlias)
{
    // Repositórios vinculados ao principal (AD-58), congelados na resolução junto com ele; nunca existem em General Mode.
    public IReadOnlyList<RepositorioCadastrado> RepositoriosVinculados { get; private init; } = [];

    public string Label => Mode == JobExecutionMode.General ? "General" : RepositoryAlias!;

    // Principal e vinculados, para status e confirmações; Label continua sendo só o principal.
    public string Descricao => RepositoriosVinculados.Count == 0
        ? Label
        : string.Join(" + ", RepositoriosVinculados.Select(vinculado => vinculado.Alias).Prepend(Label));

    public static JobExecutionContext General(string workingDirectory) =>
        new(JobExecutionMode.General, workingDirectory, null);

    public static JobExecutionContext Repository(string alias, string workingDirectory,
        IReadOnlyList<RepositorioCadastrado>? vinculados = null) =>
        new(JobExecutionMode.Repository, workingDirectory, alias) { RepositoriosVinculados = [.. vinculados ?? []] };

    public bool Equals(JobExecutionContext? other) =>
        other is not null && Mode == other.Mode && WorkingDirectory == other.WorkingDirectory &&
        RepositoryAlias == other.RepositoryAlias && RepositoriosVinculados.SequenceEqual(other.RepositoriosVinculados);

    public override int GetHashCode() => HashCode.Combine(Mode, WorkingDirectory, RepositoryAlias,
        RepositoriosVinculados.Count);
}
