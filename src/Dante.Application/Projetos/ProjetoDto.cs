namespace Dante.Application.Projetos;

// Entrada e saída do CRUD (AD-37). Na criação e na atualização, Id, AliasDoRepositorio e Arquivado são ignorados: o
// domínio gera a identidade, todo projeto nasce ativo e sem repositório, e associação e estado mudam só pelas
// operações próprias do AppService. O espaço é definido na criação e não muda depois.
public sealed record ProjetoDto
{
    public Guid Id { get; init; }
    public Guid IdEspacoDeConhecimento { get; init; }
    public string Nome { get; init; } = string.Empty;
    public string? Descricao { get; init; }
    public string? AliasDoRepositorio { get; init; }
    public bool Arquivado { get; init; }
}
