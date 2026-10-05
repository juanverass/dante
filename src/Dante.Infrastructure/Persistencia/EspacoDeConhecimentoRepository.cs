using Dante.Application.EspacosDeConhecimento;
using Dante.Domain.EspacosDeConhecimento;
using Microsoft.EntityFrameworkCore;
namespace Dante.Infrastructure.Persistencia;

public sealed class EspacoDeConhecimentoRepository(DanteDbContext context) : Repository<EspacoDeConhecimento>(context), IEspacoDeConhecimentoRepository
{
    public async Task<IReadOnlyList<EspacoDeConhecimento>> ListarDoUsuarioAsync(Guid idUsuario, string? trechoDoNome,
        bool incluirArquivados, int limite, CancellationToken cancellationToken = default)
    {
        if (idUsuario == Guid.Empty || limite is < 1 or > 100) throw new ArgumentException("Filtro inválido.");
        var query = DbSet.Where(x => x.IdUsuario == idUsuario);
        if (!incluirArquivados) query = query.Where(x => x.Estado == EstadoDoEspacoDeConhecimento.Ativo);
        if (!string.IsNullOrWhiteSpace(trechoDoNome)) query = query.Where(x => x.Nome.ToLower().Contains(trechoDoNome.ToLower()));
        return await query.OrderBy(x => x.Nome).ThenBy(x => x.Id).Take(limite).ToListAsync(cancellationToken);
    }
}
