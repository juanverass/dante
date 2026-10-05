using Dante.Application.Comum;
using Dante.Domain.Conhecimentos;

namespace Dante.Application.Conhecimentos;

public interface IConhecimentoRepository : IRepository<Conhecimento>
{
    // Escopo obrigatório; implementação filtra antes de limitar. ValidoEm aplica o intervalo temporal.
    Task<IReadOnlyList<Conhecimento>> ListarDoEspacoAsync(ConhecimentoSearchDto filtro,
        CancellationToken cancellationToken = default);
}
