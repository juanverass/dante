using Dante.Application.QualidadeDoBrain;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Conhecimentos;
using Dante.Domain.RelacoesDeConhecimento;
using Microsoft.EntityFrameworkCore;
using Dante.Infrastructure.Data;
namespace Dante.Infrastructure.QualidadeDoBrain;

public sealed class ConsultaDeQualidadePostgreSql(DanteDbContext contexto) : IConsultaDeQualidade
{
    private IQueryable<Conhecimento> Escopo(AcessoAoBrain acesso)=>contexto.Conhecimentos.Where(x=>x.IdEspacoDeConhecimento==acesso.IdEspacoDeConhecimento && x.IdProjeto==acesso.IdProjeto);
    public async Task<IReadOnlyList<Conhecimento>> ListarAsync(AcessoAoBrain acesso,int deslocamento,int limite,CancellationToken cancellationToken=default)=>
        await Escopo(acesso).OrderBy(x=>x.Id).Skip(deslocamento).Take(limite).ToListAsync(cancellationToken);
    public async Task<IReadOnlyList<RelacaoDeConhecimento>> ListarRelacoesAsync(AcessoAoBrain acesso,IReadOnlyList<Guid> ids,int limite,CancellationToken cancellationToken=default)=>
        await contexto.RelacoesDeConhecimento.Where(x=>x.IdEspacoDeConhecimento==acesso.IdEspacoDeConhecimento && x.IdProjeto==acesso.IdProjeto &&
            (ids.Contains(x.IdOrigem)||ids.Contains(x.IdDestino))).OrderBy(x=>x.Id).Take(limite).ToListAsync(cancellationToken);
    public async Task<IReadOnlyList<Conhecimento>> FiltrarElegiveisParaContextoAsync(AcessoAoBrain acesso,IReadOnlyList<Guid> ids,DateTimeOffset instante,CancellationToken cancellationToken=default)
    {
        if(ids.Count>200) throw new ArgumentException("Muitos candidatos de contexto.");
        return await Escopo(acesso).AsNoTracking().Where(x=>ids.Contains(x.Id) && x.Status!=StatusDoConhecimento.Inativo && x.Status!=StatusDoConhecimento.Substituido &&
            (x.ValidoDesde==null||x.ValidoDesde<=instante)&&(x.ValidoAte==null||x.ValidoAte>instante) &&
            (x.Sensibilidade<Sensibilidade.Confidencial||x.Sensibilidade==Sensibilidade.Confidencial&&acesso.PermitirConfidencial) &&
            !contexto.RelacoesDeConhecimento.IgnoreQueryFilters().Any(r=>r.IdEspacoDeConhecimento==acesso.IdEspacoDeConhecimento && r.IdProjeto==acesso.IdProjeto && r.Tipo==TipoDeRelacao.Contradiz && r.ResolvidaEm==null && (r.IdOrigem==x.Id||r.IdDestino==x.Id)))
            .OrderBy(x=>x.Id).ToListAsync(cancellationToken);
    }
    public async Task<IReadOnlyList<Conhecimento>> ElegiveisParaContextoAsync(AcessoAoBrain acesso,DateTimeOffset instante,int limite,CancellationToken cancellationToken=default)=>
        await Escopo(acesso).Where(x=>x.Status!=StatusDoConhecimento.Inativo && x.Status!=StatusDoConhecimento.Substituido &&
            (x.ValidoDesde==null||x.ValidoDesde<=instante)&&(x.ValidoAte==null||x.ValidoAte>instante) &&
            (x.Sensibilidade<Sensibilidade.Confidencial||x.Sensibilidade==Sensibilidade.Confidencial&&acesso.PermitirConfidencial) &&
            !contexto.RelacoesDeConhecimento.IgnoreQueryFilters().Any(r=>r.IdEspacoDeConhecimento==acesso.IdEspacoDeConhecimento && r.IdProjeto==acesso.IdProjeto && r.Tipo==TipoDeRelacao.Contradiz && r.ResolvidaEm==null && (r.IdOrigem==x.Id||r.IdDestino==x.Id)))
            .OrderBy(x=>x.Status==StatusDoConhecimento.Confirmado?0:1).ThenByDescending(x=>x.AtualizadoEm).ThenBy(x=>x.Id).Take(limite).ToListAsync(cancellationToken);
}
