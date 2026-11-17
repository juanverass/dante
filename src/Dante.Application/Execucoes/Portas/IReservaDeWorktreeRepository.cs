using Dante.Application.Comum;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Reservas de worktree (AD-71, #307). A contagem de capacidade só vale dentro da transação que chamou
// TravarCapacidadeAsync: a trava transacional serializa reservas entre processos e Workers até o commit, e os índices
// únicos parciais do banco são a segunda barreira (caminho, branch do repositório e alias da execução entre ativas).
public interface IReservaDeWorktreeRepository : IRepository<ReservaDeWorktree>
{
    // Rastreadas, em ordem de criação (principal primeiro).
    Task<IReadOnlyList<ReservaDeWorktree>> ListarDaExecucaoAsync(Guid idExecucao,
        CancellationToken cancellationToken = default);

    // pg_advisory_xact_lock: exige transação aberta; liberada no commit ou rollback.
    Task TravarCapacidadeAsync(CancellationToken cancellationToken = default);

    // Execuções distintas (exceto a informada) com reserva ativa neste host e que ainda podem escrever: interrompidas e
    // bloqueadas não têm escritor e só voltam reservando de novo.
    Task<int> ContarExecucoesEscrevendoAsync(string maquina, Guid excetoExecucao,
        CancellationToken cancellationToken = default);

    // Idem, no mesmo checkout principal (repositório), em qualquer host.
    Task<int> ContarExecucoesEscrevendoNoRepositorioAsync(string caminhoDoRepositorio, Guid excetoExecucao,
        CancellationToken cancellationToken = default);

    // Rastreadas: reservas deste host nos estados pedidos (limpeza e recuperação).
    Task<IReadOnlyList<ReservaDeWorktree>> ListarDoHostAsync(string maquina,
        IReadOnlyCollection<EstadoDaReservaDeWorktree> estados, CancellationToken cancellationToken = default);

    // Sem rastreamento: todos os caminhos já reservados neste host, em qualquer estado (órfãs são os diretórios fora disto).
    Task<IReadOnlyList<string>> ListarCaminhosDoHostAsync(string maquina, CancellationToken cancellationToken = default);
}
