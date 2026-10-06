using Dante.Application.CapturaDeConhecimento;
using Dante.Domain.CapturaDeConhecimento;
using Microsoft.EntityFrameworkCore;
using Dante.Infrastructure.Data;
using Dante.Infrastructure.Persistence;
namespace Dante.Infrastructure.Modulos.CapturaDeConhecimento;

public sealed class CandidatoDeConhecimentoRepository(DanteDbContext context) : Repository<CandidatoDeConhecimento>(context), ICandidatoDeConhecimentoRepository
{
    public Task<CandidatoDeConhecimento?> ObterEquivalenteAsync(CandidatoDeConhecimento c, CancellationToken ct = default) =>
        DbSet.SingleOrDefaultAsync(x => x.IdEspacoDeConhecimento == c.IdEspacoDeConhecimento && x.IdProjeto == c.IdProjeto && x.Impressao == c.Impressao, ct);
    public async Task<IReadOnlyList<CandidatoDeConhecimento>> ListarPendentesAsync(Guid idEspaco, Guid? idProjeto, int limite, CancellationToken ct = default)
    {
        if (idEspaco == Guid.Empty || idProjeto == Guid.Empty || limite is < 1 or > 100) throw new ArgumentException("Filtro inválido.");
        return await DbSet.Where(x => x.IdEspacoDeConhecimento == idEspaco && x.IdProjeto == idProjeto && x.Estado == EstadoDoCandidato.Pendente)
            .OrderBy(x => x.Id).Take(limite).ToListAsync(ct);
    }
}
