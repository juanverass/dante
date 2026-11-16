using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Leitura de uma execução do ledger com etapas, dependências e eventos-chave.
public sealed record ExecucaoDto
{
    public Guid Id { get; init; }
    public string Codigo { get; init; } = "";
    public long Numero { get; init; }
    public TipoDeExecucao Tipo { get; init; }
    public EstadoDaExecucao Estado { get; init; }
    public string Canal { get; init; } = "";
    public long IdDoUsuarioNoCanal { get; init; }
    public string Contexto { get; init; } = "";
    public string? Objetivo { get; init; }
    public string? Erro { get; init; }
    public string? Motivo { get; init; }
    public DateTimeOffset CriadaEm { get; init; }
    public DateTimeOffset? IniciadaEm { get; init; }
    public DateTimeOffset? EncerradaEm { get; init; }
    public IReadOnlyList<EtapaDaExecucaoDto> Etapas { get; init; } = [];
    public IReadOnlyList<DependenciaDeEtapaDto> Dependencias { get; init; } = [];
    public IReadOnlyList<EventoDeOrquestracaoDto> Eventos { get; init; } = [];
}
