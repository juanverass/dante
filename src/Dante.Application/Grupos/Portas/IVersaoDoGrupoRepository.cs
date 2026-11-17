using Dante.Application.Comum;
using Dante.Domain.Grupos;

namespace Dante.Application.Grupos;

// Versões e participantes são imutáveis: a porta só adiciona e lê, sempre com os participantes carregados.
public interface IVersaoDoGrupoRepository : IRepository<VersaoDoGrupo>
{
    Task<VersaoDoGrupo?> ObterComParticipantesAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VersaoDoGrupo>> ListarDoGrupoAsync(Guid idGrupo, CancellationToken cancellationToken = default);
}
