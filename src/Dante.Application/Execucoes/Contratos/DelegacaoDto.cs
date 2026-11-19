using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Pedido de delegação feito pela etapa em curso de uma execução (#309, PR 2). A identidade de quem pede (execução e
// etapa de origem) e o proprietário vêm de quem chama o caso de uso — pelo dante_coordenacao (PR 3), do escopo fixado
// pelo orquestrador na sessão autenticada, nunca do texto do agente. Do agente vêm só o destinatário (handle), o contrato do catálogo
// fechado, a entrada, o objetivo e os critérios; repositório, worktree, dono, canal e permissões nunca.
public sealed record SolicitacaoDeDelegacaoDto(
    ProprietarioDaExecucao Proprietario,
    Guid IdExecucaoOrigem,
    Guid IdEtapaOrigem,
    string Destinatario,
    string Contrato,
    string Entrada,
    string Objetivo,
    string? Criterios = null,
    int? MinutosParaIniciar = null,
    int? MinutosDeDuracao = null,
    bool Escrita = false);

// Retrato de uma delegação gravada. Resultado: o JSON canônico validado pelo contrato, só numa Respondida.
public sealed record DelegacaoDto(
    Guid Id,
    Guid IdExecucaoRaiz,
    Guid IdExecucaoOrigem,
    Guid IdEtapaOrigem,
    Guid IdExecucaoSubordinada,
    string CodigoDaSubordinada,
    string Destinatario,
    Guid IdVersaoDestinataria,
    string Contrato,
    EstadoDaDelegacao Estado,
    string? Motivo,
    string? Resultado,
    string? HashDoResultado,
    Guid? IdSessaoDoResultado,
    DateTimeOffset CriadaEm,
    DateTimeOffset? AceitaEm,
    DateTimeOffset? IniciadaEm,
    DateTimeOffset? EncerradaEm);

// Delegação com o último marco de progresso registrado pelo executor da subordinada (nulo antes do primeiro).
public sealed record AcompanhamentoDaDelegacaoDto(DelegacaoDto Delegacao, MarcoDaDelegacao? UltimoMarco);

// Nova: false quando a mesma unidade já existia para o mesmo destinatário (a original, como está).
public sealed record DelegacaoSolicitadaDto(DelegacaoDto Delegacao, bool Nova);

// Marcos do executor da subordinada, gravados como evento DelegacaoProgrediu: só o marco, nunca conteúdo.
public enum MarcoDaDelegacao
{
    SessaoAberta,
    TurnoEnviado,
    ResultadoRecebido
}

// O que o executor da subordinada recebe: só o pedido autorizado da delegação, nunca o ledger inteiro. Contexto é o
// contexto congelado da origem (AD-11/58), herdado pela subordinada; Versao é a versão destinatária congelada.
public sealed record ExecucaoDelegadaDto(
    DelegacaoDto Delegacao,
    string CodigoDaOrigem,
    string Canal,
    long IdDoUsuarioNoCanal,
    string Contexto,
    string ObjetivoRedigido,
    string? Criterios,
    string Entrada,
    string SchemaDaSaida,
    int MinutosDeDuracao,
    Dante.Application.Agentes.VersaoDoAgenteDto Versao);

// Conferência antes de qualquer processo. Recusa: nada é entregue ao agente e a delegação termina (Falhou, ou Expirada
// quando o motivo é prazo).
public sealed record ConferenciaDaDelegacaoDto(ExecucaoDelegadaDto? Execucao, string? Recusa, bool Expirada = false);

// Resultado entregue pela sessão vinculada à etapa da subordinada (o JSON do contrato, pela ferramenta entregar_resultado
// do dante_coordenacao desde o PR 3).
public sealed record EntregaDaDelegacaoDto(string Resultado, long NumeroDaSessao, string? HashDoArtefatoLido = null);

// Estado gravado da delegação depois de uma gravação do executor; Retomada: a origem saiu da espera nesta transação.
public sealed record DesfechoDaDelegacaoDto(EstadoDaDelegacao Estado, string? Motivo, bool Retomada);

// A etapa de retomada da origem: o pedido congelado da origem e os desfechos das delegações da etapa anterior.
public sealed record RetomadaDaOrigemDto(
    Guid IdExecucao,
    string Codigo,
    string Canal,
    long IdDoUsuarioNoCanal,
    string Contexto,
    string? ObjetivoRedigido,
    string Handle,
    Dante.Application.Agentes.VersaoDoAgenteDto Versao,
    IReadOnlyList<DesfechoEntregueDto> Desfechos);

public sealed record DesfechoEntregueDto(string CodigoDaSubordinada, string Destinatario, string Contrato,
    EstadoDaDelegacao Estado, string? Resultado, string? Motivo);

// A delegação não começou dentro do prazo para iniciar, ou passou da duração máxima: o executor a encerra Expirada.
public sealed class PrazoDaDelegacaoExcedidoException(string mensagem) : InvalidOperationException(mensagem);
