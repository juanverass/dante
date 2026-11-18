namespace Dante.Application.Execucoes;

public enum TipoDeDecisaoHumana
{
    Aprovar,
    Corrigir,
    Recusar
}

// Decisão da pessoa no gate. Aprovar e corrigir exigem o hash do artefato exibido (ao menos 12 caracteres); corrigir
// exige a instrução; recusar aceita um motivo opcional.
public sealed record DecisaoHumanaDto(TipoDeDecisaoHumana Tipo, string? HashConfirmado = null, string? Texto = null);

public enum ResultadoDaDecisao
{
    NaoEncontrada,
    SemPedidoPendente,
    Aprovada,
    CorrecaoAgendada,
    Recusada
}

public sealed record ResultadoDaDecisaoDto(ResultadoDaDecisao Resultado, string? Codigo = null, int? Rodada = null);
