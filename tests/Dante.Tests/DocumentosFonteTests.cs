using Dante.Application;
using Dante.Application.BuscaDoBrain;
using Dante.Application.CapturaDeConhecimento;
using Dante.Application.Conhecimentos;
using Dante.Application.DocumentosFonte;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Conhecimentos;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Infrastructure;
using Dante.Infrastructure.DocumentosFonte;
using Dante.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
namespace Dante.Tests;

public sealed class DocumentosFonteTests
{
    [PostgreSqlFact]
    public async Task MarkdownFonteCandidatoProvaReimportacaoERemocaoSaoDistintos()
    {
        await using var banco = await Banco.CriarAsync(); var e = new EspacoDeConhecimento(Guid.NewGuid(), "Trabalho");
        await using(var c=banco.Contexto()) { c.Add(e); await c.SaveChangesAsync(); }
        using var provider=Provider(banco.ConnectionString); using var scope=EscoposBrainDeTeste.Criar(provider,e.IdUsuario,e.Id);
        var acesso=new AcessoAoBrain(e.IdUsuario,e.Id,null);var fontes=scope.ServiceProvider.GetRequiredService<DocumentoFonteAppService>();
        var busca=scope.ServiceProvider.GetRequiredService<BuscaDoBrainAppService>();
        var md="# Procedimento\n\nResolvido deadlock usando ordem consistente de locks. 🧠\n";
        var fonte=await fontes.ImportarAsync(acesso,"nota:locks","markdown",md);
        var resultado=Assert.Single((await busca.BuscarAsync(acesso,new(){Texto="deadlock"})).Resultados);
        Assert.Equal(OrigemDoResultado.FonteBruta,resultado.Origem);Assert.Equal(md,resultado.Fonte!.Conteudo);
        Assert.Contains(fonte.Hash,resultado.Fonte.Referencia);Assert.Equal(fonte.Id,resultado.Fonte.IdDocumento);
        await using(var c=banco.Contexto()) { Assert.Empty(await c.Conhecimentos.ToListAsync()); }
        var candidato=await fontes.GerarCandidatoAsync(acesso,fonte.Id,1,0,TipoDeConhecimento.Procedimento);
        var captura=scope.ServiceProvider.GetRequiredService<ICapturaDeConhecimentoAppService>();
        var p=new ProvenienciaDto{IdResponsavel=e.IdUsuario,Origem="confirmação explícita",ReferenciaDaFonte=resultado.Fonte.Referencia,RevisaoDaFonte=fonte.Hash,TrechoDaFonte=md};
        var id=await captura.ConfirmarAsync(e.Id,null,candidato.Id,candidato.Revisao,p);
        Assert.Equal(1,(await fontes.ImportarAsync(acesso,"nota:locks","markdown",md,revisaoEsperada:1)).Revisao);
        var atual=await fontes.ImportarAsync(acesso,"nota:locks","markdown","# Novo\nTratamento atualizado de deadlock.",revisaoEsperada:1);
        Assert.Equal(2,atual.Revisao);Assert.NotEqual(fonte.Hash,atual.Hash);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>fontes.ImportarAsync(acesso,"nota:locks","markdown","alterado",revisaoEsperada:1));
        await Assert.ThrowsAsync<ArgumentException>(()=>fontes.GerarCandidatoAsync(acesso,fonte.Id,1,0));
        await fontes.RemoverAsync(acesso,fonte.Id,2);
        Assert.Empty(await fontes.ListarAsync(acesso));
        Assert.All((await busca.BuscarAsync(acesso,new(){Texto="deadlock"})).Resultados,r=>Assert.Equal(OrigemDoResultado.Conhecimento,r.Origem));
        await using var verificar=banco.Contexto();var salvo=await verificar.Conhecimentos.SingleAsync(x=>x.Id==id);
        Assert.Equal(StatusDoConhecimento.Confirmado,salvo.Status);Assert.Contains(salvo.Historico,r=>r.Proveniencia.TrechoDaFonte==md&&r.Proveniencia.RevisaoDaFonte==fonte.Hash);
        Assert.Equal("",(await verificar.Set<Dante.Domain.DocumentosFonte.DocumentoFonte>().SingleAsync()).Conteudo);
        Assert.Empty(await verificar.Database.SqlQuery<Guid>($"SELECT id_documento AS \"Value\" FROM brain_index.partes_fontes").ToListAsync());
    }
    [PostgreSqlFact]
    public async Task PartesReconstruiveisUnicodeIsolamentoESensibilidade()
    {
        await using var banco=await Banco.CriarAsync();var e=new EspacoDeConhecimento(Guid.NewGuid(),"Meu");var outro=new EspacoDeConhecimento(Guid.NewGuid(),"Outro");
        await using(var c=banco.Contexto()){c.AddRange(e,outro);await c.SaveChangesAsync();}
        using var provider=Provider(banco.ConnectionString);using var scope=EscoposBrainDeTeste.Criar(provider,e.IdUsuario,e.Id);
        var fontes=scope.ServiceProvider.GetRequiredService<DocumentoFonteAppService>();var indice=scope.ServiceProvider.GetRequiredService<IIndiceDeFontes>();
        var acesso=new AcessoAoBrain(e.IdUsuario,e.Id,null,true,true);
        var texto=new string('a',1439)+"🧠 deadlock "+new string('b',1700);
        var f=await fontes.ImportarAsync(acesso,"nota:unicode","markdown",texto);
        var parte=await indice.ObterTrechoAsync(acesso,f.Id,1,1);Assert.NotNull(parte);Assert.Equal(1440,parte.Inicio);
        Assert.Equal(string.Concat(texto.EnumerateRunes().Skip(parte.Inicio).Take(1600).Select(x=>x.ToString())),parte.Conteudo);
        await fontes.ImportarAsync(acesso,"nota:secret","texto","segredoreservado",Sensibilidade.Secreto);
        await fontes.ImportarAsync(acesso,"nota:conf","texto","confidencialreservado",Sensibilidade.Confidencial);
        var busca=scope.ServiceProvider.GetRequiredService<BuscaDoBrainAppService>();
        Assert.Empty((await busca.BuscarAsync(acesso,new(){Texto="segredoreservado"})).Resultados);
        Assert.Empty((await busca.BuscarAsync(acesso with{PermitirConfidencial=false},new(){Texto="confidencialreservado"})).Resultados);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>fontes.ListarAsync(acesso with{IdEspacoDeConhecimento=outro.Id}));
        await Assert.ThrowsAsync<ArgumentException>(()=>fontes.ImportarAsync(acesso,"nota:senha","texto","senha=segredoteste"));
        await using(var c=banco.Contexto()){await c.Database.ExecuteSqlRawAsync("TRUNCATE brain_index.partes_fontes");}
        await fontes.ReconstruirAsync(acesso);
        Assert.Equal(parte,await indice.ObterTrechoAsync(acesso,f.Id,1,1));
        Assert.Empty((await busca.BuscarAsync(acesso,new(){Texto="segredoreservado"})).Resultados);
        await using var confirmar=banco.Contexto();Assert.Equal(1,(await confirmar.Set<Dante.Domain.DocumentosFonte.DocumentoFonte>().SingleAsync(x=>x.Id==f.Id)).Revisao);
    }
    [Fact]
    public async Task ArquivoExplicitamenteSelecionadoExigeRaizFormatoETamanhoValidos()
    {
        var root=Path.Combine(Path.GetTempPath(),"brain-fontes-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var c=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["DANTE_BRAIN_IMPORT_ROOT"]=root}).Build();
            var leitor=new LeitorDeFonteLocal(c);var path=Path.Combine(root,"documento.md");await File.WriteAllTextAsync(path,"# Nota\n🧠");
            var f=await leitor.LerAsync(path);Assert.Equal("markdown",f.Formato);Assert.Equal("# Nota\n🧠",f.Conteudo);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>leitor.LerAsync(Path.Combine(root,"..","fora.txt")));
            await File.WriteAllTextAsync(Path.Combine(root,"bin.exe"),"texto");await Assert.ThrowsAsync<ArgumentException>(()=>leitor.LerAsync(Path.Combine(root,"bin.exe")));
            await File.WriteAllBytesAsync(path,new byte[1000001]);await Assert.ThrowsAsync<ArgumentException>(()=>leitor.LerAsync(path));
            if(!OperatingSystem.IsWindows()){File.CreateSymbolicLink(Path.Combine(root,"link.md"),path);await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>leitor.LerAsync(Path.Combine(root,"link.md")));}
        }
        finally{Directory.Delete(root,true);}
    }

    [PostgreSqlFact]
    public async Task EmbeddingsDePartesRespeitamVersaoModeloEProvedorExterno()
    {
        await using var banco=await Banco.CriarAsync(); var e=new EspacoDeConhecimento(Guid.NewGuid(),"Fontes");
        await using(var c=banco.Contexto()){c.Add(e);await c.SaveChangesAsync();await new Dante.Infrastructure.BuscaDoBrain.IndiceDeBuscaPostgreSql(c).PrepararVetoresAsync();}
        var gerador=new Gerador();using var provider=Provider(banco.ConnectionString,gerador);using var scope=EscoposBrainDeTeste.Criar(provider,e.IdUsuario,e.Id);
        var acesso=new AcessoAoBrain(e.IdUsuario,e.Id,null,true,true);var fontes=scope.ServiceProvider.GetRequiredService<DocumentoFonteAppService>();
        var fonte=await fontes.ImportarAsync(acesso,"nota:principal","texto","conhecimento da fonte");
        await fontes.ImportarAsync(acesso,"nota:conf","texto","confidencial da fonte",Sensibilidade.Confidencial);
        await fontes.ImportarAsync(acesso,"nota:secret","texto","secret://host/CHAVE",Sensibilidade.Secreto);
        var busca=scope.ServiceProvider.GetRequiredService<BuscaDoBrainAppService>();Assert.Equal(1,await busca.ReindexarAsync(acesso));
        Assert.DoesNotContain(gerador.Textos,t=>t.Contains("confidencial")||t.Contains("secret://"));
        var resultado=Assert.Single((await busca.BuscarAsync(acesso,new(){Texto="semelhanca"})).Resultados);
        Assert.Equal(fonte.Id,resultado.Fonte!.IdDocumento);Assert.Equal(0,resultado.ScoreLexical);Assert.True(resultado.ScoreSemantico>0.99);
        gerador.Versao="2";Assert.Empty((await busca.BuscarAsync(acesso,new(){Texto="semelhanca"})).Resultados);Assert.Equal(1,await busca.ReindexarAsync(acesso));
        await fontes.ImportarAsync(acesso,"nota:principal","texto","nova revisão",revisaoEsperada:1);
        Assert.Empty((await busca.BuscarAsync(acesso,new(){Texto="semelhanca"})).Resultados);Assert.Equal(1,await busca.ReindexarAsync(acesso));
        Assert.Equal(2,Assert.Single((await busca.BuscarAsync(acesso,new(){Texto="semelhanca"})).Resultados).Fonte!.Revisao);
    }
    [PostgreSqlFact]
    public async Task AtualizacaoConcorrenteNaoPerdeFonteNemMisturaIndice()
    {
        await using var banco=await Banco.CriarAsync();var e=new EspacoDeConhecimento(Guid.NewGuid(),"Fontes");
        await using(var c=banco.Contexto()){c.Add(e);await c.SaveChangesAsync();}
        using var provider=Provider(banco.ConnectionString);using var sa=EscoposBrainDeTeste.Criar(provider,e.IdUsuario,e.Id);using var sb=EscoposBrainDeTeste.Criar(provider,e.IdUsuario,e.Id);
        var acesso=new AcessoAoBrain(e.IdUsuario,e.Id,null);var a=sa.ServiceProvider.GetRequiredService<DocumentoFonteAppService>();var b=sb.ServiceProvider.GetRequiredService<DocumentoFonteAppService>();
        await a.ImportarAsync(acesso,"nota:concorrente","texto","original");await b.ListarAsync(acesso);
        await a.ImportarAsync(acesso,"nota:concorrente","texto","vencedor",revisaoEsperada:1);
        await Assert.ThrowsAsync<Dante.Application.Comum.ConflitoDeConcorrenciaException>(()=>b.ImportarAsync(acesso,"nota:concorrente","texto","perdido",revisaoEsperada:1));
        using var verificar=EscoposBrainDeTeste.Criar(provider,e.IdUsuario,e.Id);
        Assert.Single((await verificar.ServiceProvider.GetRequiredService<BuscaDoBrainAppService>().BuscarAsync(acesso,new(){Texto="vencedor"})).Resultados);
        Assert.Empty((await verificar.ServiceProvider.GetRequiredService<BuscaDoBrainAppService>().BuscarAsync(acesso,new(){Texto="perdido"})).Resultados);
    }
    private sealed class Gerador : IGeradorDeEmbedding
    {
        public string Versao="1"; public List<string> Textos=[]; public bool Externo=>true;
        public ModeloEmbedding? Modelo=>new("teste","fontes",Versao,3);
        public Task<float[]?> GerarAsync(string texto,CancellationToken cancellationToken=default){Textos.Add(texto);return Task.FromResult<float[]?>([1,0,0]);}
    }
    private static ServiceProvider Provider(string connection, IGeradorDeEmbedding? gerador=null)
    {
        var services=new ServiceCollection().AddApplication().AddInfrastructure(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["ConnectionStrings:Dante"]=connection}).Build());
        if(gerador is not null) services.AddSingleton(gerador);
        return services.BuildServiceProvider();
    }
}
