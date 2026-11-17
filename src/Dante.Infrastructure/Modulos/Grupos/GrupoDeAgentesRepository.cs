using Dante.Application.Grupos;
using Dante.Domain.Grupos;
using Dante.Infrastructure.Data.Orquestracao;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dante.Infrastructure.Modulos.Grupos;

public sealed class GrupoDeAgentesRepository(OrquestracaoDbContext context)
    : Repository<GrupoDeAgentes>(context), IGrupoDeAgentesRepository
{
    public Task<GrupoDeAgentes?> ObterPorNomeAsync(string nome, CancellationToken cancellationToken = default) =>
        DbSet.SingleOrDefaultAsync(x => x.Nome == nome, cancellationToken);

    public async Task<IReadOnlyList<GrupoDeAgentes>> ListarAsync(bool incluirArquivados,
        CancellationToken cancellationToken = default) =>
        await DbSet.Where(x => incluirArquivados || x.Estado != EstadoDoGrupo.Arquivado)
            .OrderBy(x => x.Nome).ToListAsync(cancellationToken);
}
