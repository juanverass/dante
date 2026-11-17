using Dante.Application.Comum;
using Dante.Domain.Grupos;

namespace Dante.Application.Grupos;

// Decisões de ativação: só acrescentadas, nunca alteradas.
public interface IAtivacaoDeVersaoDoGrupoRepository : IRepository<AtivacaoDeVersaoDoGrupo>
{
    Task<IReadOnlyList<AtivacaoDeVersaoDoGrupo>> ListarDoGrupoAsync(Guid idGrupo,
        CancellationToken cancellationToken = default);
}
