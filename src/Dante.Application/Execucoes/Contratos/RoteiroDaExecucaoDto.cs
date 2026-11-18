using Dante.Domain.Execucoes;
using Dante.Domain.Grupos;

namespace Dante.Application.Execucoes;

// Snapshot de um participante, como a execução o congelou.
public sealed record ParticipanteDaExecucaoDto(string Papel, string Handle, Guid IdVersaoDoAgente, int NumeroDaVersao,
    string HashDaConfiguracao, bool PodeEscrever, string? Responsabilidades);

// Uma etapa do roteiro: passo, rodada, papel e estado.
public sealed record PassoExecutadoDto(Guid IdEtapa, int Numero, TipoDePassoDoRoteiro Passo, int Rodada, string? Papel,
    EstadoDaEtapa Estado, int Tentativa);

// Pedido de aprovação, pendente ou decidido. Motivo: instrução de correção ou motivo da recusa, já redigidos.
public sealed record PedidoDeAprovacaoDto(Guid Id, int Rodada, string HashDoArtefato, EstadoDoPedidoDeAprovacao Estado,
    long? IdDoDecisor, DateTimeOffset? DecididoEm, string? Motivo, DateTimeOffset CriadoEm);

// Estado durável de um Roteiro: o que o orquestrador precisa para continuar depois de um reinício, sem nada bruto.
public sealed record RoteiroDaExecucaoDto
{
    public Guid IdExecucao { get; init; }
    public string Codigo { get; init; } = "";
    public EstadoDaExecucao Estado { get; init; }
    // Repositório congelado na criação (alias e checkout principal); nunca diretório de trabalho de um agente.
    public string AliasDoRepositorio { get; init; } = "";
    public string CheckoutDoRepositorio { get; init; } = "";
    public string Canal { get; init; } = "";
    public long IdDoUsuarioNoCanal { get; init; }
    public string NomeDoGrupo { get; init; } = "";
    public int NumeroDaVersaoDoGrupo { get; init; }
    public string HashDaVersaoDoGrupo { get; init; } = "";
    public string HashDoSnapshot { get; init; } = "";
    public IReadOnlyList<PassoDoRoteiro> Passos { get; init; } = [];
    public int RodadasDeRevisaoMaximas { get; init; }
    public string ObjetivoRedigido { get; init; } = "";
    public bool ObjetivoAlteradoPelaRedacao { get; init; }
    public string HashDoObjetivoOriginal { get; init; } = "";
    public IReadOnlyList<ParticipanteDaExecucaoDto> Participantes { get; init; } = [];
    public IReadOnlyList<PassoExecutadoDto> Etapas { get; init; } = [];
    public IReadOnlyList<PedidoDeAprovacaoDto> Pedidos { get; init; } = [];

    public PedidoDeAprovacaoDto? PedidoPendente => Pedidos.SingleOrDefault(p => p.Estado == EstadoDoPedidoDeAprovacao.Pendente);

    // Instrução da correção pedida no gate mais recente, para o corretor da rodada seguinte.
    public string? UltimaInstrucaoDeCorrecao => Pedidos.LastOrDefault(p =>
        p.Estado == EstadoDoPedidoDeAprovacao.CorrecaoSolicitada)?.Motivo;
}
