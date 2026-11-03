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
    public async Task<IReadOnlyList<CandidatoDeConhecimento>> ListarPendentesAsync(Guid idEspaco, Guid? idProjeto, int limite, CancellationToken ct = default, int deslocamento = 0,
        IReadOnlyList<string>? tags = null, string? texto = null)
    {
        if (idEspaco == Guid.Empty || idProjeto == Guid.Empty || limite is < 1 or > 100 || deslocamento is < 0 or > 10000) throw new ArgumentException("Filtro inválido.");
        if (tags is not null && (tags.Count > 50 || tags.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 100)) ||
            texto is not null && (string.IsNullOrWhiteSpace(texto) || texto.Length > 2000)) throw new ArgumentException("Filtro inválido.");
        var consulta = DbSet.AsQueryable();
        if (tags is { Count: > 0 } || texto is not null)
        {
            var exigidas = tags?.ToArray() ?? [];
            // Metadados pertencem ao último ato JSON, não a todos os atos históricos. FromSql é
            // componível: os filtros globais de autorização continuam ativos antes de Skip/Take.
            consulta = DbSet.FromSqlInterpolated($"""
                SELECT *, xmin FROM brain_data.candidatos_de_conhecimento
                WHERE NOT EXISTS (
                    SELECT 1 FROM unnest({exigidas}::text[]) exigida
                    WHERE NOT EXISTS (
                        SELECT 1 FROM jsonb_array_elements_text(
                            COALESCE(NULLIF(historico -> -1 -> 'Tags', 'null'::jsonb), '[]'::jsonb)) existente
                        WHERE lower(existente) = lower(exigida)))
                  AND ({texto}::text IS NULL
                    OR strpos(lower(COALESCE(historico -> -1 ->> 'Titulo', '')), lower({texto})) > 0
                    OR strpos(lower(conteudo), lower({texto})) > 0)
                """);
        }
        return await consulta.Where(x => x.IdEspacoDeConhecimento == idEspaco && x.IdProjeto == idProjeto && x.Estado == EstadoDoCandidato.Pendente)
            .OrderBy(x => x.Id).Skip(deslocamento).Take(limite).ToListAsync(ct);
    }
}
