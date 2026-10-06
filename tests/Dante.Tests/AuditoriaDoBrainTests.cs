using System.Text.Json;
using Dante.Application;
using Dante.Application.AuditoriaDoBrain;
using Dante.Application.Conhecimentos;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Conhecimentos;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Domain.Projetos;
using Dante.Domain.RelacoesDeConhecimento;
using Dante.Domain.DocumentosFonte;
using Dante.Infrastructure;
using Dante.Infrastructure.AuditoriaDoBrain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
namespace Dante.Tests;

public sealed class AuditoriaDoBrainTests
{
    [PostgreSqlFact]
    public async Task ExportEspacoIncluiProjetosIdsRelacoesProvasEExcluiItensObsoletos()
    {
        await using var banco=await Banco.CriarAsync();var e=new EspacoDeConhecimento(Guid.NewGuid(),"Espaço");var projeto=new Projeto(e.Id,"Projeto");
        var a=Novo(e.Id,null,"procedimento",e.IdUsuario);var b=Novo(e.Id,null,"aprendizado",e.IdUsuario);var p=Novo(e.Id,projeto.Id,"decisão de projeto",e.IdUsuario);
        a.Confirmar(1,a.Proveniencia,DateTimeOffset.UtcNow);var inativo=Novo(e.Id,null,"inativo",e.IdUsuario);inativo.Invalidar(1,inativo.Proveniencia,DateTimeOffset.UtcNow);
        var expirado=Novo(e.Id,null,"expirado",e.IdUsuario);expirado.Corrigir(1,expirado.Tipo,expirado.Conteudo,null,null,expirado.Sensibilidade,null,DateTimeOffset.UtcNow.AddDays(-1),["audit"],expirado.Proveniencia,DateTimeOffset.UtcNow);
        var relacao=new RelacaoDeConhecimento(a,b,TipoDeRelacao.RelacionadoA,a.Proveniencia,DateTimeOffset.UtcNow);
        var fonte=new DocumentoFonte(e.Id,null,"nota:prova","markdown","# Original",Sensibilidade.Pessoal,e.IdUsuario,DateTimeOffset.UtcNow);
        await using(var c=banco.Contexto()){c.AddRange(e,projeto,a,b,p,inativo,expirado,relacao,fonte);await c.SaveChangesAsync();}
        using var provider=Provider(banco.ConnectionString);using var scope=EscoposBrainDeTeste.Criar(provider,e.IdUsuario,e.Id);
        var s=scope.ServiceProvider.GetRequiredService<InspecaoDoBrainAppService>();var acesso=new AcessoAoBrain(e.IdUsuario,e.Id,null);
        var export=await s.ExportarAsync(acesso);var dto=JsonSerializer.Deserialize<AuditoriaDoBrainDto>(export.Json)!;
        Assert.Equal(1,dto.VersaoFormato);Assert.False(dto.TemMais);Assert.Equal(3,dto.Conhecimentos.Count);Assert.Single(dto.Projetos);
        Assert.Equal(projeto.Id,dto.Projetos[0].Id);Assert.Equal(relacao.Id,Assert.Single(dto.Relacoes).Id);
        Assert.All(dto.Relacoes,r=>{Assert.Contains(dto.Conhecimentos,k=>k.Id==r.IdOrigem);Assert.Contains(dto.Conhecimentos,k=>k.Id==r.IdDestino);});
        var item=Assert.Single(dto.Conhecimentos,k=>k.Id==a.Id);Assert.Equal(StatusDoConhecimento.Confirmado,item.Status);Assert.Equal("evidência",item.Proveniencia.TrechoDaFonte);Assert.Equal(2,item.Historico.Count);
        Assert.Equal(fonte.Id,Assert.Single(dto.Fontes).Documento.Id);Assert.Contains(a.Id.ToString("D"),export.Markdown);Assert.DoesNotContain("Claude",export.Json);Assert.DoesNotContain("Codex",export.Json);
        var audit=await s.InspecionarAsync(acesso,new(){IncluirInativos=true,Origem="revisão",Tag="AUDIT"});Assert.Equal(5,audit.Conhecimentos.Count);
        Assert.Single((await s.InspecionarAsync(acesso,new(){Status=StatusDoConhecimento.Confirmado})).Conhecimentos);
        using var escopoProjeto=EscoposBrainDeTeste.Criar(provider,e.IdUsuario,e.Id,projeto.Id);
        var somenteProjeto=JsonSerializer.Deserialize<AuditoriaDoBrainDto>((await escopoProjeto.ServiceProvider.GetRequiredService<InspecaoDoBrainAppService>().ExportarAsync(acesso with{IdProjeto=projeto.Id})).Json)!;
        Assert.Equal(p.Id,Assert.Single(somenteProjeto.Conhecimentos).Id);Assert.Empty(somenteProjeto.Fontes);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>s.ExportarAsync(acesso with{IdUsuario=Guid.NewGuid()}));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>s.ExportarAsync(acesso with{IdProjeto=projeto.Id}));
    }
    [PostgreSqlFact]
    public async Task RedactionProtegeConteudoHistoricoFontesENomesNosDoisFormatos()
    {
        await using var banco=await Banco.CriarAsync();var e=new EspacoDeConhecimento(Guid.NewGuid(),"senha=nomeprotegido");
        var a=Novo(e.Id,null,"historicosuperrestrito",e.IdUsuario,Sensibilidade.Secreto);
        a.Corrigir(1,a.Tipo,"atual permitido",null,null,Sensibilidade.Publico,null,null,[],a.Proveniencia,DateTimeOffset.UtcNow);
        var secreto=Novo(e.Id,null,"segredoclassificado",e.IdUsuario,Sensibilidade.Secreto);
        var conf=Novo(e.Id,null,"confidencialclassificado",e.IdUsuario,Sensibilidade.Confidencial);
        var legado=Novo(e.Id,null,"token=legadoreal123",e.IdUsuario);
        var fonte=new DocumentoFonte(e.Id,null,"nota:legada","texto","password=legadoreal456",Sensibilidade.Pessoal,e.IdUsuario,DateTimeOffset.UtcNow);
        await using(var c=banco.Contexto()){c.AddRange(e,a,secreto,conf,legado,fonte);await c.SaveChangesAsync();}
        using var provider=Provider(banco.ConnectionString);using var scope=EscoposBrainDeTeste.Criar(provider,e.IdUsuario,e.Id);
        var s=scope.ServiceProvider.GetRequiredService<InspecaoDoBrainAppService>();var acesso=new AcessoAoBrain(e.IdUsuario,e.Id,null);
        var export=await s.ExportarAsync(acesso);
        foreach(var segredo in new[]{"nomeprotegido","historicosuperrestrito","segredoclassificado","confidencialclassificado","legadoreal123","legadoreal456"})
        {Assert.DoesNotContain(segredo,export.Json);Assert.DoesNotContain(segredo,export.Markdown);}
        var autorizado=await s.ExportarAsync(acesso with{PermitirConfidencial=true,PermitirSecreto=true});Assert.Contains("confidencialclassificado",autorizado.Json);Assert.DoesNotContain("historicosuperrestrito",autorizado.Json);Assert.DoesNotContain("segredoclassificado",autorizado.Json);
        Assert.Contains((await s.InspecionarAsync(acesso with{PermitirSecreto=true})).Conhecimentos,k=>k.Id==secreto.Id);
    }
    [PostgreSqlFact]
    public async Task EspacoSemProjetoArquivadoContinuaAuditavelEPaginacaoNaoExportaParcial()
    {
        await using var banco=await Banco.CriarAsync();var e=new EspacoDeConhecimento(Guid.NewGuid(),"Pessoal");e.Arquivar();
        var a=Novo(e.Id,null,"um",e.IdUsuario);var b=Novo(e.Id,null,"dois",e.IdUsuario);
        await using(var c=banco.Contexto()){c.AddRange(e,a,b);await c.SaveChangesAsync();}
        using var provider=Provider(banco.ConnectionString);using var scope=EscoposBrainDeTeste.Criar(provider,e.IdUsuario,e.Id);
        var s=scope.ServiceProvider.GetRequiredService<InspecaoDoBrainAppService>();var acesso=new AcessoAoBrain(e.IdUsuario,e.Id,null);
        var pagina=await s.InspecionarAsync(acesso,new(){Limite=1});Assert.True(pagina.TemMais);Assert.Single(pagina.Conhecimentos);
        var outra=await s.InspecionarAsync(acesso,new(){Limite=1,Deslocamento=1});Assert.NotEqual(pagina.Conhecimentos[0].Id,outra.Conhecimentos[0].Id);
        var export=JsonSerializer.Deserialize<AuditoriaDoBrainDto>((await s.ExportarAsync(acesso)).Json)!;Assert.Empty(export.Projetos);Assert.True(export.Espaco.Arquivado);Assert.Equal(2,export.Conhecimentos.Count);
        Assert.Single(await s.ListarEspacosAsync());
    }
    [Fact]
    public async Task ArquivosDeExportacaoNaoSobrescrevemExistentesENaoDeixamParIncompleto()
    {
        var root=Path.Combine(Path.GetTempPath(),"brain-export-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var prefix=Path.Combine(root,"export");await GravadorDeExportacao.SalvarAsync(new("markdown","json"),prefix);
            Assert.Equal("json",await File.ReadAllTextAsync(prefix+".json"));Assert.Equal("markdown",await File.ReadAllTextAsync(prefix+".md"));
            await Assert.ThrowsAsync<IOException>(()=>GravadorDeExportacao.SalvarAsync(new("outro","outro"),prefix));Assert.Equal("json",await File.ReadAllTextAsync(prefix+".json"));
            var incompleto=Path.Combine(root,"ocupado");await File.WriteAllTextAsync(incompleto+".md","existente");
            await Assert.ThrowsAsync<IOException>(()=>GravadorDeExportacao.SalvarAsync(new("novo","novo"),incompleto));Assert.False(File.Exists(incompleto+".json"));Assert.Equal("existente",await File.ReadAllTextAsync(incompleto+".md"));
            if(!OperatingSystem.IsWindows())Assert.Equal(UnixFileMode.UserRead|UnixFileMode.UserWrite,File.GetUnixFileMode(prefix+".json"));
        }
        finally{Directory.Delete(root,true);}
    }
    private static Conhecimento Novo(Guid espaco,Guid? projeto,string texto,Guid user,Sensibilidade classe=Sensibilidade.Pessoal)=>new(espaco,projeto,TipoDeConhecimento.Nota,texto,null,StatusDoConhecimento.Inferido,0.7,classe,null,null,["audit"],new(user,"revisão manual","fonte:teste","1","evidência"),DateTimeOffset.UtcNow);
    private static ServiceProvider Provider(string connection)=>new ServiceCollection().AddApplication().AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["ConnectionStrings:Dante"]=connection}).Build()).BuildServiceProvider();
}
