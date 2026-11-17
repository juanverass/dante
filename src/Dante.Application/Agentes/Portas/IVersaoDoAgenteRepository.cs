using Dante.Application.Comum;
using Dante.Domain.Agentes;

namespace Dante.Application.Agentes;

// Versões são imutáveis: a porta só adiciona e lê (Atualizar/Remover existem na base genérica, o banco os recusa).
public interface IVersaoDoAgenteRepository : IRepository<VersaoDoAgente>
{
    Task<IReadOnlyList<VersaoDoAgente>> ListarDoAgenteAsync(Guid idAgente, CancellationToken cancellationToken = default);
}
