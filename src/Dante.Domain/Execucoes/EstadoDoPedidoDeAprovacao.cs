namespace Dante.Domain.Execucoes;

// Pendente até a decisão humana; as demais são terminais e imutáveis (trigger). Cancelado: a execução foi cancelada
// enquanto esperava.
public enum EstadoDoPedidoDeAprovacao
{
    Pendente,
    Aprovado,
    CorrecaoSolicitada,
    Recusado,
    Cancelado
}
