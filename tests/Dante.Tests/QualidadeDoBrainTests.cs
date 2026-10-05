using Dante.Application;
using Dante.Application.Conhecimentos;
using Dante.Application.QualidadeDoBrain;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Conhecimentos;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Domain.RelacoesDeConhecimento;
using Dante.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
namespace Dante.Tests;

public sealed class QualidadeDoBrainTests
{
    [Fact]
    public void ConsolidacaoPreservaCadaFonteSemReescreverConteudoConfirmado()
    {
        var e=Guid.NewGuid();var a=Novo(e,"conteúdo",fonte:"a"); var b=Novo(e,"conteúdo",fonte:"b");
        b.Corrigir(1,b.Tipo,b.Conteudo,null,null,b.Sensibilidade,null,null,[],new(Guid.NewGuid(),"nova fonte","c",null,"evidência c"),DateTimeOffset.UtcNow);
        a.Confirmar(1,a.Proveniencia,DateTimeOffset.UtcNow); var p=new ProvenienciaDoConhecimento(Guid.NewGuid(),"decisão","manual",null,"mesmo fato");
        a.IncorporarFontesDe([b],2,p,DateTimeOffset.UtcNow);
        Assert.Equal("conteúdo",a.Conteudo); Assert.Equal(StatusDoConhecimento.Confirmado,a.Status);
        Assert.Contains(a.Historico,r=>r.Proveniencia.ReferenciaDaFonte=="b"); Assert.Contains(a.Historico,r=>r.Proveniencia.ReferenciaDaFonte=="c");
        Assert.Equal(3,a.Historico.Select(x=>x.Proveniencia.ReferenciaDaFonte).Where(x=>x is "a" or "b" or "c").Distinct().Count());
        Assert.Throws<InvalidOperationException>(()=>a.IncorporarFontesDe([b],2,p,DateTimeOffset.UtcNow));
    }
    [Fact]
    public void InferenciaNaoSubstituiConfirmacaoENaoReduzClassificacao()
    {
        var e=Guid.NewGuid();var inferido=Novo(e,"inferencia");var confirmado=Novo(e,"fato");confirmado.Confirmar(1,confirmado.Proveniencia,DateTimeOffset.UtcNow);
        Assert.Throws<ArgumentException>(()=>inferido.IncorporarFontesDe([confirmado],1,inferido.Proveniencia,DateTimeOffset.UtcNow));
        var secreto=Novo(e,"restrito",Sensibilidade.Secreto);
        Assert.Throws<ArgumentException>(()=>confirmado.IncorporarFontesDe([secreto],2,confirmado.Proveniencia,DateTimeOffset.UtcNow));
        Assert.Equal(2,confirmado.Revisao);
    }
    [Fact]
    public void ResolucaoDeContradicaoExigeItemConfirmadoEPreservaAtoOriginal()
    {
        var e=Guid.NewGuid();var a=Novo(e,"sim");var b=Novo(e,"não");var r=new RelacaoDeConhecimento(a,b,TipoDeRelacao.Contradiz,a.Proveniencia,DateTimeOffset.UtcNow);
        Assert.Throws<InvalidOperationException>(()=>r.ResolverContradicao(a,a.Proveniencia,DateTimeOffset.UtcNow));
        a.Confirmar(1,a.Proveniencia,DateTimeOffset.UtcNow);var decisao=new ProvenienciaDoConhecimento(Guid.NewGuid(),"usuário","fonte",null,"evidência");
        r.ResolverContradicao(a,decisao,DateTimeOffset.UtcNow);
        Assert.Equal(a.Id,r.IdConhecimentoEscolhido);Assert.Equal(decisao,r.ProvenienciaDaResolucao);Assert.Equal(a.Proveniencia,r.Proveniencia);
        Assert.Throws<InvalidOperationException>(()=>r.ResolverContradicao(b,decisao,DateTimeOffset.UtcNow));
    }
    [PostgreSqlFact]
    public async Task RevisaoConsolidacaoConflitoResolucaoEValidadeSaoAuditaveis()
    {
        await using var banco=await Banco.CriarAsync();var e=new EspacoDeConhecimento(Guid.NewGuid(),"Trabalho");
        var a=Novo(e.Id,"Guid em agrupamentos",fonte:"a");a.Confirmar(1,a.Proveniencia,DateTimeOffset.UtcNow);
        var b=Novo(e.Id,"Guid em agrupamentos",fonte:"b");
        var x=Novo(e.Id,"valor A",json:"{\"chave\":\"timeout\",\"valor\":30}");x.Confirmar(1,x.Proveniencia,DateTimeOffset.UtcNow);
        var y=Novo(e.Id,"valor B",json:"{\"chave\":\"timeout\",\"valor\":60}");y.Confirmar(1,y.Proveniencia,DateTimeOffset.UtcNow);
        var expirado=Novo(e.Id,"passado",ate:DateTimeOffset.UtcNow.AddDays(-1));var semFonte=Novo(e.Id,"manual",fonte:null);
        var secreto=Novo(e.Id,"secret://host/TOKEN",Sensibilidade.Secreto);
        await using(var c=banco.Contexto()){c.AddRange(e,a,b,x,y,expirado,semFonte,secreto);await c.SaveChangesAsync();}
        var acesso=new AcessoAoBrain(e.IdUsuario,e.Id,null);var decisao=Decisao(e.IdUsuario);using var provider=Provider(banco.ConnectionString);
        Guid idConflito;
        using(var scope=provider.CreateScope())
        {
            var s=scope.ServiceProvider.GetRequiredService<ManutencaoDoBrainAppService>();var report=await s.RevisarAsync(acesso,confirmarAntesDe:DateTimeOffset.UtcNow.AddMinutes(1));
            Assert.Contains(report.Achados,r=>r.Problema==ProblemaDeQualidade.PossivelDuplicata&&(r.IdConhecimento==a.Id||r.IdRelacionado==a.Id));
            Assert.Contains(report.Achados,r=>r.Problema==ProblemaDeQualidade.PossivelContradicao);
            Assert.Contains(report.Achados,r=>r.IdConhecimento==expirado.Id&&r.Problema==ProblemaDeQualidade.ForaDaValidade);
            Assert.Contains(report.Achados,r=>r.IdConhecimento==semFonte.Id&&r.Problema==ProblemaDeQualidade.SemFonteReferenciada);
            Assert.Contains(report.Achados,r=>r.Problema==ProblemaDeQualidade.ConfirmacaoAnteriorAoLimite);
            Assert.True((await s.RevisarAsync(acesso,limite:1)).LimiteAtingido);
            await s.ConsolidarAsync(acesso,new(a.Id,2),[new(b.Id,1)],decisao);
            idConflito=await s.MarcarContradicaoAsync(acesso,new(x.Id,2),new(y.Id,2),decisao);
            var elegiveis=await s.SelecionarParaContextoAsync(acesso);
            Assert.DoesNotContain(elegiveis,r=>r.Id==b.Id||r.Id==x.Id||r.Id==y.Id||r.Id==expirado.Id||r.Id==secreto.Id);
            Assert.Equal(StatusDoConhecimento.Confirmado,elegiveis[0].Status);
            var conflito=Assert.Single((await s.RevisarAsync(acesso)).Conflitos);Assert.Null(conflito.ResolvidoEm);
            await s.InvalidarAsync(acesso,new(expirado.Id,1),decisao);
            await s.ResolverConflitoAsync(acesso,idConflito,new(x.Id,2),new(y.Id,2),decisao);
        }
        using(var scope=provider.CreateScope())
        {
            var s=scope.ServiceProvider.GetRequiredService<ManutencaoDoBrainAppService>();var report=await s.RevisarAsync(acesso);
            Assert.NotNull(Assert.Single(report.Conflitos).ResolvidoEm);Assert.Equal(x.Id,report.Conflitos[0].IdEscolhido);
            var selecionados=await s.SelecionarParaContextoAsync(acesso);Assert.Contains(selecionados,r=>r.Id==x.Id);Assert.DoesNotContain(selecionados,r=>r.Id==y.Id);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>s.RevisarAsync(acesso with{IdUsuario=Guid.NewGuid()}));
        }
        await using(var c=banco.Contexto())
        {
            var salvo=await c.Conhecimentos.SingleAsync(k=>k.Id==a.Id);Assert.Equal("Guid em agrupamentos",salvo.Conteudo);Assert.Contains(salvo.Historico,r=>r.Proveniencia.ReferenciaDaFonte=="b");
            var original=await c.Conhecimentos.SingleAsync(k=>k.Id==b.Id);Assert.Equal(StatusDoConhecimento.Substituido,original.Status);Assert.Equal(a.Id,original.IdConhecimentoSubstituto);Assert.Equal("b",original.Historico[0].Proveniencia.ReferenciaDaFonte);
            var relacao=await c.RelacoesDeConhecimento.SingleAsync(r=>r.Id==idConflito);Assert.Equal(e.IdUsuario,relacao.ProvenienciaDaResolucao!.IdResponsavel);
            Assert.Equal(StatusDoConhecimento.Inativo,(await c.Conhecimentos.SingleAsync(k=>k.Id==expirado.Id)).Status);
        }
    }
    [PostgreSqlFact]
    public async Task ConcorrenciaDeMergeNaoPerdeFontesNemDuplicaRelacoes()
    {
        await using var banco=await Banco.CriarAsync();var e=new EspacoDeConhecimento(Guid.NewGuid(),"Espaço");var a=Novo(e.Id,"igual",fonte:"a");var b=Novo(e.Id,"igual",fonte:"b");
        await using(var c=banco.Contexto()){c.AddRange(e,a,b);await c.SaveChangesAsync();}
        using var provider=Provider(banco.ConnectionString);using var sa=provider.CreateScope();using var sb=provider.CreateScope();
        var acesso=new AcessoAoBrain(e.IdUsuario,e.Id,null);var primeiro=sa.ServiceProvider.GetRequiredService<ManutencaoDoBrainAppService>();var segundo=sb.ServiceProvider.GetRequiredService<ManutencaoDoBrainAppService>();
        await primeiro.RevisarAsync(acesso);await segundo.RevisarAsync(acesso);
        await primeiro.ConsolidarAsync(acesso,new(a.Id,1),[new(b.Id,1)],Decisao(e.IdUsuario));
        await Assert.ThrowsAsync<Dante.Application.Comum.ConflitoDeConcorrenciaException>(()=>segundo.ConsolidarAsync(acesso,new(a.Id,1),[new(b.Id,1)],Decisao(e.IdUsuario)));
        await using var verificar=banco.Contexto();Assert.Single(await verificar.RelacoesDeConhecimento.ToListAsync());
        Assert.Contains((await verificar.Conhecimentos.SingleAsync(k=>k.Id==a.Id)).Historico,r=>r.Proveniencia.ReferenciaDaFonte=="b");
    }
    [PostgreSqlFact]
    public async Task ConflitoPodeSerResolvidoDepoisDaInvalidacaoSemReabrirHistorico()
    {
        await using var banco=await Banco.CriarAsync();var e=new EspacoDeConhecimento(Guid.NewGuid(),"Espaço");
        var a=Novo(e.Id,"opção A");var b=Novo(e.Id,"opção B");a.Confirmar(1,a.Proveniencia,DateTimeOffset.UtcNow);
        await using(var c=banco.Contexto()){c.AddRange(e,a,b);await c.SaveChangesAsync();}
        using var provider=Provider(banco.ConnectionString);using var scope=provider.CreateScope();
        var s=scope.ServiceProvider.GetRequiredService<ManutencaoDoBrainAppService>();var acesso=new AcessoAoBrain(e.IdUsuario,e.Id,null);var p=Decisao(e.IdUsuario);
        var conflito=await s.MarcarContradicaoAsync(acesso,new(a.Id,2),new(b.Id,1),p);
        await s.InvalidarAsync(acesso,new(b.Id,1),p);Assert.Empty(await s.SelecionarParaContextoAsync(acesso));
        await s.ResolverConflitoAsync(acesso,conflito,new(a.Id,2),new(b.Id,2),p);
        Assert.Equal(a.Id,Assert.Single(await s.SelecionarParaContextoAsync(acesso)).Id);
        await using var verificar=banco.Contexto();Assert.Equal(StatusDoConhecimento.Inativo,(await verificar.Conhecimentos.SingleAsync(x=>x.Id==b.Id)).Status);
    }
    private static Conhecimento Novo(Guid e,string conteudo,Sensibilidade s=Sensibilidade.Trabalho,string? fonte="fonte",string? json=null,DateTimeOffset? ate=null)=>new(e,null,
        TipoDeConhecimento.Fato,conteudo,json,StatusDoConhecimento.Inferido,null,s,null,ate,[],new(Guid.NewGuid(),"teste",fonte,null,fonte is null?null:"evidência"),DateTimeOffset.UtcNow);
    private static ProvenienciaDto Decisao(Guid user)=>new(){IdResponsavel=user,Origem="decisão explícita",ReferenciaDaFonte="revisão manual",TrechoDaFonte="evidência escolhida pelo usuário"};
    private static ServiceProvider Provider(string connection)=>new ServiceCollection().AddApplication().AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["ConnectionStrings:Dante"]=connection}).Build()).BuildServiceProvider();
}
