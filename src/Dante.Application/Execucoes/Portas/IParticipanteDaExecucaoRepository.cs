using Dante.Application.Comum;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Snapshot dos participantes de um Roteiro: imutável, só adiciona e lê.
public interface IParticipanteDaExecucaoRepository : IRepository<ParticipanteDaExecucao>
{
    Task<IReadOnlyList<ParticipanteDaExecucao>> ListarDaExecucaoAsync(Guid idExecucao,
        CancellationToken cancellationToken = default);
}
