using Dante.Application.Comum;
using Dante.Domain.Conhecimentos;

namespace Dante.Application.Conhecimentos;

public interface IConhecimentoRepository : IRepository<Conhecimento>
{
    // Escopo obrigatório; implementação filtra antes de limitar. ValidoEm aplica o intervalo temporal.
    Task<IReadOnlyList<Conhecimento>> ListarDoEspacoAsync(ConhecimentoSearchDto filtro,
        CancellationToken cancellationToken = default);
    // Conhecimento armazenado no escopo exato, sem inativos/substituídos (#148): quantidade e caracteres do conteúdo.
    Task<(int Quantidade, long Caracteres)> MedirEscopoAsync(Guid idEspaco, Guid? idProjeto,
        CancellationToken cancellationToken = default);
}
