namespace Dante.Domain.Execucoes;

// Pendente até a decisão humana; as demais são terminais e imutáveis (trigger). Cancelado: a execução foi cancelada
// enquanto esperava. Substituido: o conteúdo da worktree deixou de ser o artefato revisado e o dono pediu uma nova
// revisão; nenhuma decisão vale para ele.
public enum EstadoDoPedidoDeAprovacao
{
    Pendente,
    Aprovado,
    CorrecaoSolicitada,
    Recusado,
    Cancelado,
    Substituido
}
