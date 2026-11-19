namespace Dante.Domain.Execucoes;

// Estados de uma delegação (AD-70, #309). Solicitada: validada pelo domínio e gravada com a execução subordinada.
// Aceita: enfileirada (validação e fila; não é o destinatário "aceitando"). EmAndamento: a subordinada começou.
// Respondida, Falhou, Expirada, Cancelada e Recusada são terminais; só Respondida tem resultado.
public enum EstadoDaDelegacao
{
    Solicitada,
    Aceita,
    EmAndamento,
    Respondida,
    Falhou,
    Expirada,
    Cancelada,
    Recusada
}

// Por que o domínio recusou uma delegação. Nada é gravado numa recusa: a linha nem chega a existir.
public enum MotivoDaRecusaDeDelegacao
{
    OrigemInvalida,
    Autodelegacao,
    Ciclo,
    ProfundidadeExcedida,
    FanOutExcedido,
    OrcamentoExcedido,
    DestinatarioNaoAutorizado,
    DestinatarioIndisponivel,
    CapacidadeInsuficiente,
    RepositorioForaDaIntersecao,
    LimitesExcedidos,
    EscritaNaoAutorizada,
    ContratoInvalido,
    UnidadeJaAtribuida,
    // Unidade de plano (AD-75): o plano não está aprovado, ou a etapa não é dele.
    PlanoNaoAprovado,
    // Unidade de plano (AD-75): coordenador ou destinatário diferente do snapshot congelado da execução.
    ForaDoSnapshot
}

public sealed class DelegacaoRecusadaException(MotivoDaRecusaDeDelegacao motivo, string mensagem)
    : InvalidOperationException(mensagem)
{
    public MotivoDaRecusaDeDelegacao Motivo { get; } = motivo;
}
