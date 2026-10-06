using Dante.Application.Comum;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.DocumentosFonte;
namespace Dante.Application.DocumentosFonte;

public interface IDocumentoFonteRepository : IRepository<DocumentoFonte>
{
    Task<DocumentoFonte?> ObterPelaOrigemAsync(AcessoAoBrain acesso, string origem, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DocumentoFonte>> ListarAsync(AcessoAoBrain acesso, int limite, CancellationToken cancellationToken = default);
}
