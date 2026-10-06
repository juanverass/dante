using Dante.Application;
using Dante.Application.BuscaDoBrain;
using Dante.Application.ConstrucaoDeContexto;
using Dante.Application.ContextosDeTrabalho;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Conhecimentos;
using Dante.Domain.ContextosDeTrabalho;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Domain.RelacoesDeConhecimento;
using Dante.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
namespace Dante.Tests;

public sealed class ConstrucaoDeContextoTests
{
    [PostgreSqlFact]
    public async Task RelevanciaPrecedenciaBudgetEInjecaoSaoRastreaveis()
    {
        await using var banco=await Banco.CriarAsync();var e=new EspacoDeConhecimento(Guid.NewGuid(),"Contexto");
        var instrucao=Novo(e,"Guid: preservar identidade de domínio.",TipoDeConhecimento.Instrucao,true);
        var decisao=Novo(e,"Guid: usar chaves globais.",TipoDeConhecimento.Decisao,true);
        var inferido=Novo(e,"Guid: hipótese sem confirmação.");var grande=Novo(e,"Guid "+new string('x',10000));var irrelevante=Novo(e,"Receita de bolo.");
        await using(var c=banco.Contexto()){c.AddRange(e,instrucao,decisao,inferido,grande,irrelevante);await c.SaveChangesAsync();}
        using var provider=Provider(banco.ConnectionString);using var scope=EscoposBrainDeTeste.Criar(provider,e.IdUsuario,e.Id);var acesso=new AcessoAoBrain(e.IdUsuario,e.Id,null);
        var pacote=await scope.ServiceProvider.GetRequiredService<ConstrutorDeContextoAppService>().ConstruirAsync(acesso,new(){Mensagem="Explique Guid",Filtros=new(){Texto="Guid"},OrcamentoDeTokens=500,LimiteDeItens=10,IncluirSnapshot=false});
        Assert.Equal(instrucao.Id,pacote.Itens[0].Id);Assert.Equal(decisao.Id,pacote.Itens[1].Id);Assert.True(pacote.Custo.TokensEstimados<=500);
        Assert.DoesNotContain(pacote.Itens,x=>x.Id==grande.Id||x.Id==irrelevante.Id);
        Assert.Contains(pacote.Registros,x=>x.Id==grande.Id&&x.Motivo.Contains("orçamento"));
        Assert.All(pacote.Itens,x=>{Assert.Contains(x.Id.ToString("D"),pacote.TextoParaInjecao);Assert.False(string.IsNullOrEmpty(x.Motivo));});
        Assert.DoesNotContain(pacote.Registros,x=>x.Estado=="injetado");
        var injetado=pacote.RegistrarInjecao(pacote.Itens.Select(x=>x.Chave).ToArray());Assert.Equal(pacote.Itens.Count,injetado.Registros.Count(x=>x.Estado=="injetado"));
        Assert.Throws<ArgumentException>(()=>pacote.RegistrarInjecao(["não selecionado"]));
        await Assert.ThrowsAsync<ArgumentException>(()=>scope.ServiceProvider.GetRequiredService<ConstrutorDeContextoAppService>().ConstruirAsync(acesso,new(){Mensagem="Guid",OrcamentoDeTokens=10}));
    }
    [PostgreSqlFact]
    public async Task RelacoesExpandemComLimiteECiclosSemObsoletosConflitosOuAcessoCruzado()
    {
        await using var banco=await Banco.CriarAsync();var e=new EspacoDeConhecimento(Guid.NewGuid(),"Contexto");
        var raiz=Novo(e,"deadlock no armazenamento",TipoDeConhecimento.Incidente);var solucao=Novo(e,"ordem consistente de locks",TipoDeConhecimento.Solucao,true);
        var aprendizado=Novo(e,"sempre adquirir locks na mesma ordem",TipoDeConhecimento.Aprendizado,true);
        var expirado=Novo(e,"deadlock expirado");expirado.Corrigir(1,expirado.Tipo,expirado.Conteudo,null,null,expirado.Sensibilidade,null,DateTimeOffset.UtcNow.AddMinutes(-1),[],expirado.Proveniencia,DateTimeOffset.UtcNow);
        var secreto=Novo(e,"deadlock reservado",classe:Sensibilidade.Secreto);var conflitado=Novo(e,"deadlock conflitante",confirmado:true);
        var relacoes=new[]{new RelacaoDeConhecimento(raiz,solucao,TipoDeRelacao.ResolvidoPor,raiz.Proveniencia,DateTimeOffset.UtcNow),new RelacaoDeConhecimento(solucao,aprendizado,TipoDeRelacao.ProduziuAprendizado,raiz.Proveniencia,DateTimeOffset.UtcNow),new RelacaoDeConhecimento(aprendizado,raiz,TipoDeRelacao.RelacionadoA,raiz.Proveniencia,DateTimeOffset.UtcNow),new RelacaoDeConhecimento(conflitado,secreto,TipoDeRelacao.Contradiz,raiz.Proveniencia,DateTimeOffset.UtcNow)};
        await using(var c=banco.Contexto()){c.AddRange(e,raiz,solucao,aprendizado,expirado,secreto,conflitado);c.AddRange(relacoes);await c.SaveChangesAsync();}
        using var provider=Provider(banco.ConnectionString);using var scope=EscoposBrainDeTeste.Criar(provider,e.IdUsuario,e.Id);var acesso=new AcessoAoBrain(e.IdUsuario,e.Id,null,true,true);
        var builder=scope.ServiceProvider.GetRequiredService<ConstrutorDeContextoAppService>();
        var pacote=await builder.ConstruirAsync(acesso,new(){Mensagem="deadlock",ProfundidadeDeRelacoes=3,OrcamentoDeTokens=2000,IncluirSnapshot=false});
        Assert.Contains(pacote.Itens,x=>x.Id==solucao.Id&&x.Motivo.Contains("Expansão"));Assert.Contains(pacote.Itens,x=>x.Id==aprendizado.Id);
        Assert.Equal(pacote.Itens.Count,pacote.Itens.Select(x=>x.Chave).Distinct().Count());
        Assert.DoesNotContain(pacote.Itens,x=>x.Id==expirado.Id||x.Id==secreto.Id||x.Id==conflitado.Id);
        Assert.Contains(pacote.Registros,x=>x.Id==conflitado.Id&&x.Motivo.Contains("conflitante"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>builder.ConstruirAsync(acesso with{IdEspacoDeConhecimento=Guid.NewGuid()},new(){Mensagem="deadlock"}));
        var filtro=await builder.ConstruirAsync(acesso,new(){Mensagem="deadlock",Filtros=new(){Tipo=TipoDeConhecimento.Solucao},IncluirSnapshot=false});Assert.Empty(filtro.Itens);
    }
    [PostgreSqlFact]
    public async Task NovaSessaoRetomaSomenteSnapshotAtivoEDeduplicaPromptResumoEItens()
    {
        await using var banco=await Banco.CriarAsync();var e=new EspacoDeConhecimento(Guid.NewGuid(),"Trabalho");var d=Novo(e,"Guid: decisão consolidada.",TipoDeConhecimento.Decisao,true);var nota=Novo(e,"Guid: resultado já presente no resumo.");
        await using(var c=banco.Contexto()){c.AddRange(e,d,nota);await c.SaveChangesAsync();}
        using var provider=Provider(banco.ConnectionString);var acesso=new AcessoAoBrain(e.IdUsuario,e.Id,null);
        using(var scope=EscoposBrainDeTeste.Criar(provider,e.IdUsuario,e.Id))
        {
            await scope.ServiceProvider.GetRequiredService<ContextoDeTrabalhoAppService>().SubstituirAsync(acesso,0,new DadosDoContexto(d.Conteudo!,"atividade atual","feito",[d.Id],[],[],[],"resultado útil"),Sensibilidade.Trabalho,"seleção manual");
        }
        using var nova=EscoposBrainDeTeste.Criar(provider,e.IdUsuario,e.Id);var pacote=await nova.ServiceProvider.GetRequiredService<ConstrutorDeContextoAppService>().ConstruirAsync(acesso,new(){Mensagem="Guid",FragmentosJaPresentes=[nota.Conteudo!],OrcamentoDeTokens=2000});
        Assert.Contains(pacote.Itens,x=>x.Id==d.Id);Assert.DoesNotContain(pacote.Itens,x=>x.Id==nota.Id);
        var snapshot=Assert.Single(pacote.Itens,x=>x.Origem=="snapshot");Assert.Contains("atividade atual",snapshot.Conteudo);Assert.DoesNotContain(d.Conteudo!,snapshot.Conteudo);Assert.Null(snapshot.Status);
        Assert.DoesNotContain("transcript",pacote.TextoParaInjecao);Assert.Contains(pacote.Registros,x=>x.Id==nota.Id&&x.Estado=="descartado");
    }

    [PostgreSqlFact]
    public async Task FontesBrutasNaoGanhamStatusNemDuplicamTrechosSobrepostos()
    {
        await using var banco=await Banco.CriarAsync();var e=new EspacoDeConhecimento(Guid.NewGuid(),"Fontes");
        await using(var c=banco.Contexto()){c.Add(e);await c.SaveChangesAsync();}
        using var provider=Provider(banco.ConnectionString);using var scope=EscoposBrainDeTeste.Criar(provider,e.IdUsuario,e.Id);
        var acesso=new AcessoAoBrain(e.IdUsuario,e.Id,null,true,true);var fontes=scope.ServiceProvider.GetRequiredService<Dante.Application.DocumentosFonte.DocumentoFonteAppService>();
        var texto="Guid "+new string('a',1450)+" Guid "+new string('b',1400);
        var f=await fontes.ImportarAsync(acesso,"nota:contexto","markdown",texto);
        var builder=scope.ServiceProvider.GetRequiredService<ConstrutorDeContextoAppService>();
        var pacote=await builder.ConstruirAsync(acesso,new(){Mensagem="Guid",OrcamentoDeTokens=4000,IncluirSnapshot=false});
        var item=Assert.Single(pacote.Itens);Assert.Equal("fonte_bruta",item.Origem);Assert.Null(item.Status);Assert.Null(item.Tipo);Assert.Contains(f.Hash,item.Referencia!);
        Assert.Contains(pacote.Registros,x=>x.Motivo.Contains("sobreposto"));
        await fontes.ImportarAsync(acesso,"nota:contexto","markdown",texto,Sensibilidade.Secreto,1);
        var vazio=await builder.ConstruirAsync(acesso,new(){Mensagem="Guid",IncluirSnapshot=false});Assert.Empty(vazio.Itens);Assert.Equal("",vazio.TextoParaInjecao);Assert.Equal(0,vazio.Custo.TokensEstimados);
    }
    private static Conhecimento Novo(EspacoDeConhecimento e,string texto,TipoDeConhecimento tipo=TipoDeConhecimento.Fato,bool confirmado=false,Sensibilidade classe=Sensibilidade.Pessoal)
    {var k=new Conhecimento(e.Id,null,tipo,texto,null,StatusDoConhecimento.Inferido,null,classe,null,null,[],new(e.IdUsuario,"manual","fonte:teste"),DateTimeOffset.UtcNow);if(confirmado)k.Confirmar(1,k.Proveniencia,DateTimeOffset.UtcNow);return k;}
    private static ServiceProvider Provider(string connection)=>new ServiceCollection().AddApplication().AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["ConnectionStrings:Dante"]=connection}).Build()).BuildServiceProvider();
}
