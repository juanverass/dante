using Dante.Application.Conhecimentos;
using Dante.Domain.Conhecimentos;
using Microsoft.EntityFrameworkCore;
using Dante.Infrastructure.Data;
using Dante.Infrastructure.Persistence;
namespace Dante.Infrastructure.Modulos;

public sealed class ConhecimentoRepository(DanteDbContext context) : Repository<Conhecimento>(context), IConhecimentoRepository
{
    public async Task<IReadOnlyList<Conhecimento>> ListarDoEspacoAsync(ConhecimentoSearchDto filtro,
        CancellationToken cancellationToken = default)
    {
        if (filtro.IdEspacoDeConhecimento == Guid.Empty || filtro.IdProjeto == Guid.Empty || filtro.Limite is < 1 or > 100)
            throw new ArgumentException("Filtro inválido.");
        var origem = filtro.Tag is null ? DbSet.AsQueryable() : DbSet.FromSqlInterpolated(
            $"SELECT c.*, c.xmin FROM brain_data.conhecimentos c WHERE EXISTS (SELECT 1 FROM unnest(c.tags) AS t(tag) WHERE lower(t.tag) = lower({filtro.Tag}))");
        var query = origem.Where(x => x.IdEspacoDeConhecimento == filtro.IdEspacoDeConhecimento);
        if (filtro.IdProjeto is not null) query = query.Where(x => x.IdProjeto == filtro.IdProjeto);
        if (filtro.SomenteSemProjeto) query = query.Where(x => x.IdProjeto == null);
        if (!filtro.IncluirInativosOuSubstituidos) query = query.Where(x => x.Status != StatusDoConhecimento.Inativo && x.Status != StatusDoConhecimento.Substituido);
        if (filtro.Tipo is not null) query = query.Where(x => x.Tipo == filtro.Tipo);
        if (filtro.Status is not null) query = query.Where(x => x.Status == filtro.Status);
        if (filtro.ValidoEm is { } instante) query = query.Where(x => (x.ValidoDesde == null || x.ValidoDesde <= instante) && (x.ValidoAte == null || x.ValidoAte > instante));
        return await query.OrderByDescending(x => x.AtualizadoEm).ThenBy(x => x.Id).Take(filtro.Limite).ToListAsync(cancellationToken);
    }
}
