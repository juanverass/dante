using Dante.Application.Comum;
using Dante.Domain.RelacoesDeConhecimento;
namespace Dante.Application.RelacoesDeConhecimento;

public interface IRelacaoDeConhecimentoRepository : IRepository<RelacaoDeConhecimento>
{
    Task<RelacaoDeConhecimento?> ObterEquivalenteAsync(RelacaoDeConhecimento relacao, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RelacaoDeConhecimento>> ListarVizinhasAsync(Guid idEspaco, Guid? idProjeto,
        IReadOnlyCollection<Guid> idsFronteira, int limite, CancellationToken cancellationToken = default);
}
