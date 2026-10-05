using Dante.Application.Projetos;
using Dante.Domain.Projetos;
using Microsoft.EntityFrameworkCore;
namespace Dante.Infrastructure.Persistencia;

public sealed class ProjetoRepository(DanteDbContext context) : Repository<Projeto>(context), IProjetoRepository
{
    public async Task<IReadOnlyList<Projeto>> ListarDoEspacoAsync(Guid idEspacoDeConhecimento, string? trechoDoNome,
        bool incluirArquivados, int limite, CancellationToken cancellationToken = default)
    {
        if (idEspacoDeConhecimento == Guid.Empty || limite is < 1 or > 100) throw new ArgumentException("Filtro inválido.");
        var query = DbSet.Where(x => x.IdEspacoDeConhecimento == idEspacoDeConhecimento);
        if (!incluirArquivados) query = query.Where(x => x.Estado == EstadoDoProjeto.Ativo);
        if (!string.IsNullOrWhiteSpace(trechoDoNome)) query = query.Where(x => x.Nome.ToLower().Contains(trechoDoNome.ToLower()));
        return await query.OrderBy(x => x.Nome).ThenBy(x => x.Id).Take(limite).ToListAsync(cancellationToken);
    }
}
