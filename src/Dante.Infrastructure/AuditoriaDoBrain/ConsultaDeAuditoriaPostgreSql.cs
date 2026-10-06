using Dante.Application.AuditoriaDoBrain;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Conhecimentos;
using Dante.Domain.DocumentosFonte;
using Dante.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;
namespace Dante.Infrastructure.AuditoriaDoBrain;

// Auditoria de um espaço inclui seus projetos; acesso operacional continua com projeto exato.
public sealed class ConsultaDeAuditoriaPostgreSql(DanteDbContext contexto, AutorizacaoDoBrain autorizacao) : IConsultaDeAuditoria
{
    public async Task<DadosDeAuditoria> ConsultarAsync(AcessoAoBrain acesso, AuditoriaDoBrainSearchDto filtro,
        bool exportacao, CancellationToken cancellationToken = default)
    {
        autorizacao.Exigir(acesso);
        if (filtro.Limite is < 1 or > 1000 || filtro.Deslocamento is < 0 or > 10000 || filtro.Origem?.Length > 200 || filtro.Tag?.Length > 100 ||
            filtro.Tipo is { } tipo && !Enum.IsDefined(tipo) || filtro.Status is { } status && !Enum.IsDefined(status)) throw new ArgumentException("Filtro de auditoria inválido.");
        await using var tx=await contexto.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,cancellationToken);
        var espaco=await contexto.EspacosDeConhecimento.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x=>
            x.Id==acesso.IdEspacoDeConhecimento && x.IdUsuario==autorizacao.Identidade!.IdUsuario && x.IdTenant==autorizacao.Identidade!.IdTenant,cancellationToken)
            ?? throw new UnauthorizedAccessException("Espaço não autorizado.");
        var projetos=await contexto.Projetos.IgnoreQueryFilters().AsNoTracking().Where(x=>x.IdEspacoDeConhecimento==espaco.Id &&
            (acesso.IdProjeto==null || x.Id==acesso.IdProjeto)).OrderBy(x=>x.Id).Take(1001).ToListAsync(cancellationToken);
        if(acesso.IdProjeto is not null && projetos.Count!=1) throw new UnauthorizedAccessException("Projeto não autorizado.");
        // Origem e tags são parâmetros; sem avaliar filtros depois da paginação.
        var itens=contexto.Conhecimentos.FromSqlInterpolated($"""
            SELECT *, xmin FROM brain_data.conhecimentos WHERE
              ({filtro.Origem}::text IS NULL OR strpos(lower(historico -> -1 -> 'Proveniencia' ->> 'Origem'),lower({filtro.Origem}))>0)
              AND ({filtro.Tag}::text IS NULL OR EXISTS(SELECT 1 FROM unnest(tags) tag WHERE lower(tag)=lower({filtro.Tag})))
            """).IgnoreQueryFilters().AsNoTracking().Where(x=>x.IdEspacoDeConhecimento==espaco.Id &&
                (acesso.IdProjeto==null || x.IdProjeto==acesso.IdProjeto) &&
                (x.Sensibilidade<Sensibilidade.Confidencial || x.Sensibilidade==Sensibilidade.Confidencial && acesso.PermitirConfidencial ||
                 !exportacao && x.Sensibilidade==Sensibilidade.Secreto && acesso.PermitirSecreto));
        if(filtro.Tipo is not null) itens=itens.Where(x=>x.Tipo==filtro.Tipo);
        if(filtro.Status is not null) itens=itens.Where(x=>x.Status==filtro.Status);
        var agora=DateTimeOffset.UtcNow;
        if(exportacao || !filtro.IncluirInativos) itens=itens.Where(x=>x.Status!=StatusDoConhecimento.Inativo && x.Status!=StatusDoConhecimento.Substituido &&
            (x.ValidoDesde==null || x.ValidoDesde<=agora) && (x.ValidoAte==null || x.ValidoAte>agora));
        var conhecidos=await itens.OrderBy(x=>x.Id).Skip(filtro.Deslocamento).Take(filtro.Limite+1).ToListAsync(cancellationToken);
        var ids=conhecidos.Take(filtro.Limite).Select(x=>x.Id).ToArray();
        var relacoes=await contexto.RelacoesDeConhecimento.IgnoreQueryFilters().AsNoTracking().Where(x=>x.IdEspacoDeConhecimento==espaco.Id &&
            ids.Contains(x.IdOrigem) && ids.Contains(x.IdDestino)).OrderBy(x=>x.Id).Take(10001).ToListAsync(cancellationToken);
        var fontes=await contexto.Set<DocumentoFonte>().IgnoreQueryFilters().AsNoTracking().Where(x=>x.IdEspacoDeConhecimento==espaco.Id &&
            (acesso.IdProjeto==null || x.IdProjeto==acesso.IdProjeto) && !x.Removido &&
            (x.Sensibilidade<Sensibilidade.Confidencial || x.Sensibilidade==Sensibilidade.Confidencial && acesso.PermitirConfidencial ||
             !exportacao && x.Sensibilidade==Sensibilidade.Secreto && acesso.PermitirSecreto))
            .OrderBy(x=>x.Id).Skip(filtro.Deslocamento).Take(filtro.Limite+1).ToListAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new(espaco,projetos.Take(1000).ToArray(),conhecidos.Take(filtro.Limite).ToArray(),relacoes.Take(10000).ToArray(),fontes.Take(filtro.Limite).ToArray(),
            projetos.Count>1000 || conhecidos.Count>filtro.Limite || fontes.Count>filtro.Limite || relacoes.Count>10000);
    }
}
