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
    Recusada,
    // O conteúdo da worktree não é mais o revisado: nada foi decidido; o gate continua pendente até uma nova revisão.
    ArtefatoDivergente,
    // Nova revisão do conteúdo atual agendada (o pedido anterior foi substituído).
    RevisaoAgendada
}

// HashAtual: o hash do conteúdo presente na worktree, quando ele diverge do revisado.
public sealed record ResultadoDaDecisaoDto(ResultadoDaDecisao Resultado, string? Codigo = null, int? Rodada = null,
    string? HashAtual = null);
