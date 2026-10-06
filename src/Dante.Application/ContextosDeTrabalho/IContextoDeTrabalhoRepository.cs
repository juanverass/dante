using Dante.Application.Comum;
using Dante.Domain.ContextosDeTrabalho;
namespace Dante.Application.ContextosDeTrabalho;

public interface IContextoDeTrabalhoRepository : IRepository<ContextoDeTrabalho>
{
    Task<ContextoDeTrabalho?> ObterDoEscopoAsync(Guid idEspaco, Guid? idProjeto, CancellationToken cancellationToken = default);
}
