using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;

namespace Dante.Worker.Execucoes;

// Executa as etapas de um tipo de execução reivindicadas pela fila (AD-67). O processador chama ExecutarAsync fora de
// qualquer transação; o executor marca o início e grava o resultado pelo controle, que confere a posse a cada
// gravação. O token é cancelado no encerramento do Worker e quando a posse se perde (heartbeat recusado): o executor
// encerra o processo e não deve produzir efeito seguinte. Execuções novas (Tarefa, #301) registram o seu executor aqui.
public interface IExecutorDeEtapa
{
    TipoDeExecucao Tipo { get; }

    // Falso quando este processo não tem como executar (o pedido do job legado só existe na memória do boot que o
    // criou): a execução não iniciada é cancelada com aviso, nunca executada às cegas.
    bool PodeExecutar(AlvoDoTrabalhoDto alvo);

    Task ExecutarAsync(AlvoDoTrabalhoDto alvo, ExecucaoSobPosse controle, CancellationToken cancellationToken);

    // Aviso ao dono quando a execução foi interrompida, bloqueada ou cancelada pela recuperação. Não pode lançar.
    Task AoInterromperAsync(ExecucaoInterrompidaDto interrupcao, CancellationToken cancellationToken) => Task.CompletedTask;
}
