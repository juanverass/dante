using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Resultado de um pedido de cancelamento (#305), neutro de interface.
public enum ResultadoDoCancelamento
{
    // Inexistente, de outro proprietário ou código inválido: indistinguíveis.
    NaoEncontrada,
    JaEncerrada,
    // Nenhum processo tinha a posse: a execução foi cancelada nesta mesma transação.
    Cancelada,
    // Um processo tem a posse: o pedido foi registrado e quem tem a posse grava Cancelada.
    Solicitada,
    // Já havia um pedido registrado: nada novo foi gravado.
    JaSolicitada,
    // Job legado em andamento: só o registro em memória do job (/cancel do legado) o encerra.
    JobEmAndamento
}

public sealed record CancelamentoDeExecucaoDto(ResultadoDoCancelamento Resultado, string? Codigo = null,
    EstadoDaExecucao? Estado = null);
