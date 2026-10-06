using Dante.Application.DocumentosFonte;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.DocumentosFonte;
using Microsoft.EntityFrameworkCore;
namespace Dante.Infrastructure.Persistencia;
public sealed class DocumentoFonteRepository(DanteDbContext c) : Repository<DocumentoFonte>(c), IDocumentoFonteRepository
{
    public Task<DocumentoFonte?> ObterPelaOrigemAsync(AcessoAoBrain acesso, string origem, CancellationToken cancellationToken = default) =>
        DbSet.SingleOrDefaultAsync(x => x.IdEspacoDeConhecimento == acesso.IdEspacoDeConhecimento && x.IdProjeto == acesso.IdProjeto && x.Origem == origem, cancellationToken);
    public async Task<IReadOnlyList<DocumentoFonte>> ListarAsync(AcessoAoBrain acesso, int limite, CancellationToken cancellationToken = default) =>
        await DbSet.Where(x => x.IdEspacoDeConhecimento == acesso.IdEspacoDeConhecimento && x.IdProjeto == acesso.IdProjeto && !x.Removido)
            .OrderBy(x => x.Origem).ThenBy(x => x.Id).Take(limite).ToListAsync(cancellationToken);
}
