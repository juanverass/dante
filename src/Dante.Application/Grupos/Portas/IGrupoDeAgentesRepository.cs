using Dante.Application.Comum;
using Dante.Domain.Grupos;

namespace Dante.Application.Grupos;

public interface IGrupoDeAgentesRepository : IRepository<GrupoDeAgentes>
{
    // Inclui arquivados: o nome nunca volta a ser usado.
    Task<GrupoDeAgentes?> ObterPorNomeAsync(string nome, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GrupoDeAgentes>> ListarAsync(bool incluirArquivados, CancellationToken cancellationToken = default);
    // Roteiros não terminais do ledger que usam alguma versão do grupo.
    Task<int> ContarExecucoesAtivasAsync(Guid idGrupo, CancellationToken cancellationToken = default);
}
