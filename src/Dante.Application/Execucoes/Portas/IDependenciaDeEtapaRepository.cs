using Dante.Application.Comum;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

public interface IDependenciaDeEtapaRepository : IRepository<DependenciaDeEtapa>
{
    Task<IReadOnlyList<DependenciaDeEtapa>> ListarDaExecucaoAsync(Guid idExecucao,
        CancellationToken cancellationToken = default);
}
