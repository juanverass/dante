namespace Dante.Application.Execucoes;

// Worktrees de execução de escrita (AD-71, #307). Reservar, marcar pronta e falhar acontecem sob posse do trabalho
// (GravarComPosseAsync, AD-67): posse perdida não grava nada. A limpeza física trata só reservas liberadas (execução
// encerrada) deste host e nunca usa --force.
public interface IReservasDeWorktreeAppService
{
    // Reserva atômica de todas as worktrees da execução (principal primeiro, depois vinculados): todas ou nenhuma.
    // Dentro de uma trava transacional que serializa processos e Workers, confere os limites global (por host) e por
    // repositório; excedidos, CapacidadeDeWorktreesIndisponivelException sem gravar. Execução que já tem reservas ativas
    // (nova tentativa, mesma ou outra geração) recebe as mesmas, nunca duplicadas; divergência ou caminho de reserva
    // encerrada: WorktreeInvalidaException.
    Task<IReadOnlyList<WorktreeDaExecucaoDto>> ReservarAsync(PosseDoTrabalho posse, Guid idEtapa,
        IReadOnlyList<WorktreePlanejada> planejadas, string maquina, CancellationToken cancellationToken = default);

    // Reaquisição do slot por uma etapa de escrita posterior da mesma execução (correção de um Roteiro, #308, decisão
    // C): as reservas que continuaram ativas durante o gate, sem replanejar a worktree. Sob a mesma trava e os mesmos
    // limites de ReservarAsync: sem capacidade, CapacidadeDeWorktreesIndisponivelException sem gravar (o processador
    // espera sem contar tentativa). Sem reserva ativa, de outro host ou nenhuma principal: WorktreeInvalidaException.
    Task<IReadOnlyList<WorktreeDaExecucaoDto>> ReadquirirAsync(PosseDoTrabalho posse, Guid idEtapa, string maquina,
        CancellationToken cancellationToken = default);

    // Todas criadas e validadas: Reservada → Pronta, sob posse.
    Task<IReadOnlyList<WorktreeDaExecucaoDto>> MarcarProntasAsync(PosseDoTrabalho posse, Guid idExecucao,
        CancellationToken cancellationToken = default);

    // Criação ou validação recusada: a do alias vira Falhou (diretório nunca reutilizado nem apagado) e as demais são
    // liberadas para a limpeza. Nenhum agente começa.
    Task FalharAsync(PosseDoTrabalho posse, Guid idExecucao, string alias, string motivo,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorktreeDaExecucaoDto>> ListarDaExecucaoAsync(Guid idExecucao,
        CancellationToken cancellationToken = default);

    // Reservas ativas de execuções já encerradas são liberadas; liberadas deste host são removidas se limpas ou
    // preservadas com motivo; diretórios desconhecidos sob a raiz são só relatados.
    Task<RelatorioDeWorktreesDto> LimparAsync(string maquina, CancellationToken cancellationToken = default);
}
