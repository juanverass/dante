using Dante.Application.RelacoesDeConhecimento;
using Dante.Domain.RelacoesDeConhecimento;
using Microsoft.EntityFrameworkCore;
using Dante.Infrastructure.Data;
using Dante.Infrastructure.Persistence;
namespace Dante.Infrastructure.Modulos.RelacoesDeConhecimento;

public sealed class RelacaoDeConhecimentoRepository(DanteDbContext context) : Repository<RelacaoDeConhecimento>(context), IRelacaoDeConhecimentoRepository
{
    public Task<RelacaoDeConhecimento?> ObterEquivalenteAsync(RelacaoDeConhecimento r, CancellationToken ct = default) =>
        DbSet.SingleOrDefaultAsync(x => x.IdEspacoDeConhecimento == r.IdEspacoDeConhecimento && x.IdOrigem == r.IdOrigem && x.IdDestino == r.IdDestino && x.Tipo == r.Tipo, ct);
    public async Task<IReadOnlyList<RelacaoDeConhecimento>> ListarVizinhasAsync(Guid idEspaco, Guid? idProjeto,
        IReadOnlyCollection<Guid> idsFronteira, int limite, CancellationToken cancellationToken = default)
    {
        if (idEspaco == Guid.Empty || idProjeto == Guid.Empty || idsFronteira.Count > 201 || limite is < 1 or > 101) throw new ArgumentException("Filtro inválido.");
        return await DbSet.Where(x => x.IdEspacoDeConhecimento == idEspaco && x.IdProjeto == idProjeto &&
            (idsFronteira.Contains(x.IdOrigem) || idsFronteira.Contains(x.IdDestino)))
            .OrderBy(x => x.CriadaEm).ThenBy(x => x.Id).Take(limite).ToListAsync(cancellationToken);
    }
}
