using Dante.Application;
using Dante.Application.BuscaDoBrain;
using Dante.Application.Conhecimentos;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Conhecimentos;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Infrastructure;
using Dante.Infrastructure.BuscaDoBrain;
using Dante.Infrastructure.Persistencia;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
namespace Dante.Tests;

public sealed class BuscaDoBrainTests
{
    [Theory]
    [InlineData(0f,0f)]
    [InlineData(float.NaN,1f)]
    public void VetorInvalidoNaoGeraIndice(float x, float y) => Assert.Throws<ArgumentException>(() => BuscaDoBrainAppService.Normalizar([x,y],2));
    [PostgreSqlFact]
    public async Task BuscaLexicalFuncionaSemExtensaoComFiltrosPaginacaoEMetadadosProtegidos()
    {
        await using var banco = await Banco.CriarAsync(); var e = new EspacoDeConhecimento(Guid.NewGuid(), "Trabalho");
        var outro = new EspacoDeConhecimento(Guid.NewGuid(), "Outro");
        var a = Novo(e.Id,"Guid em agrupamentos",tags:["csharp"]); var b = Novo(e.Id,"Guid para entidades",tags:["csharp"]);
        var secreto = Novo(e.Id,"Guid segredo",Sensibilidade.Secreto); var conf = Novo(e.Id,"Guid confidencial",Sensibilidade.Confidencial);
        var invalido = Novo(e.Id,"Guid obsoleto"); invalido.Invalidar(1,invalido.Proveniencia,DateTimeOffset.UtcNow);
        var expirado = Novo(e.Id,"Guid expirado",ate:DateTimeOffset.UtcNow.AddHours(-1));
        await using(var c = banco.Contexto()) { c.AddRange(e,outro,a,b,secreto,conf,invalido,expirado,Novo(outro.Id,"Guid externo")); await c.SaveChangesAsync(); }
        using var provider = Provider(banco.ConnectionString,new Gerador()); using var scope = provider.CreateScope();
        var busca = scope.ServiceProvider.GetRequiredService<BuscaDoBrainAppService>(); var acesso = new AcessoAoBrain(e.IdUsuario,e.Id,null);
        var resultado = await busca.BuscarAsync(acesso,new() { Texto="Guid",Tags=["CSHARP"],Limite=1 });
        Assert.Equal("lexical",resultado.Modo); Assert.True(resultado.TemMais); Assert.Single(resultado.Resultados);
        Assert.Equal(OrigemDoResultado.Conhecimento,resultado.Resultados[0].Origem); Assert.True(resultado.Resultados[0].ScoreLexical>0);
        var pagina2=await busca.BuscarAsync(acesso,new() { Texto="Guid",Tags=["csharp"],Limite=1,Deslocamento=1 });
        Assert.NotEqual(resultado.Resultados[0].Item.Id,pagina2.Resultados[0].Item.Id);
        Assert.Equal(2,(await busca.BuscarAsync(acesso,new() { Texto="Guid" })).Resultados.Count);
        Assert.Equal(3,(await busca.BuscarAsync(acesso with { PermitirConfidencial=true },new() { Texto="Guid" })).Resultados.Count);
        var metadata = Assert.Single((await busca.BuscarAsync(acesso,new() { IdConhecimento=secreto.Id })).Resultados);
        Assert.True(metadata.Item.ConteudoProtegido); Assert.Null(metadata.Item.Conteudo); Assert.Null(metadata.Item.ReferenciaDaFonte);
        Assert.Empty((await busca.BuscarAsync(acesso,new() { Texto="Guid",Tipo=TipoDeConhecimento.Decisao })).Resultados);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>busca.BuscarAsync(acesso with { IdUsuario=outro.IdUsuario },new() { Texto="Guid" }));
        await Assert.ThrowsAsync<ArgumentException>(()=>busca.BuscarAsync(acesso,new() { Texto="x",Limite=101 }));
    }
    [PostgreSqlFact]
    public async Task SemanticaReindexacaoMudancaDeModeloEFalhaPreservamCanonico()
    {
        await using var banco = await Banco.CriarAsync(); var e = new EspacoDeConhecimento(Guid.NewGuid(),"Trabalho");
        var a = Novo(e.Id,"relação opcional não existe"); var b=Novo(e.Id,"imagem e fotografia"); var segredo=Novo(e.Id,"relação privada",Sensibilidade.Secreto);
        await using(var c=banco.Contexto()) { c.AddRange(e,a,b,segredo); await c.SaveChangesAsync(); }
        var gerador=new Gerador(); using var provider=Provider(banco.ConnectionString,gerador); var acesso=new AcessoAoBrain(e.IdUsuario,e.Id,null);
        using(var scope=provider.CreateScope())
        {
            var indice=scope.ServiceProvider.GetRequiredService<IndiceDeBuscaPostgreSql>(); await indice.PrepararVetoresAsync();
            var busca=scope.ServiceProvider.GetRequiredService<BuscaDoBrainAppService>();
            Assert.Equal(2,await busca.ReindexarAsync(acesso)); Assert.Equal(0,await busca.ReindexarAsync(acesso));
            Assert.DoesNotContain(gerador.Textos,x=>x.Contains("privada"));
            var semantico=await busca.BuscarAsync(acesso,new() { Texto="associação ausente" });
            var item=Assert.Single(semantico.Resultados); Assert.Equal(a.Id,item.Item.Id); Assert.Equal(0,item.ScoreLexical); Assert.True(item.ScoreSemantico>0.99);
            Assert.Equal("hibrido",semantico.Modo);
            var misto=Assert.Single((await busca.BuscarAsync(acesso,new() { Texto="relação opcional" })).Resultados);
            Assert.True(misto.ScoreLexical>0); Assert.True(misto.ScoreSemantico>0);
            gerador.Versao="2"; Assert.Empty((await busca.BuscarAsync(acesso,new() { Texto="associação ausente" })).Resultados);
            Assert.Equal(2,await busca.ReindexarAsync(acesso)); Assert.Equal(2,await busca.ReindexarAsync(acesso,reconstruir:true));
            gerador.Indisponivel=true; Assert.Equal("lexical",(await busca.BuscarAsync(acesso,new() { Texto="relação" })).Modo);
        }
        await using(var c=banco.Contexto())
        {
            var salvo=(await c.Conhecimentos.SingleAsync(x=>x.Id==a.Id)); Assert.Equal(1,salvo.Revisao);
            salvo.Corrigir(1,salvo.Tipo,"fotografia",null,null,salvo.Sensibilidade,null,null,[],salvo.Proveniencia,DateTimeOffset.UtcNow); await c.SaveChangesAsync();
        }
        gerador.Indisponivel=false;
        using(var scope=provider.CreateScope())
        {
            var busca=scope.ServiceProvider.GetRequiredService<BuscaDoBrainAppService>();
            Assert.Empty((await busca.BuscarAsync(acesso,new() { Texto="associação ausente" })).Resultados);
            Assert.Equal(1,await busca.ReindexarAsync(acesso));
        }
    }
    [Fact]
    public async Task AdapterHttpTrataFalhaEValidaModeloSemEnviarSemConfiguracao()
    {
        var handler=new Handler(); using var http=new HttpClient(handler);
        using var gerador=new GeradorDeEmbeddingHttp(http,new ConfigurationBuilder().Build());
        Assert.Null(await gerador.GerarAsync("texto")); Assert.Equal(0,handler.Chamadas);
        var cfg=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["DANTE_BRAIN_EMBEDDING_ENDPOINT"]="http://localhost/embeddings",["DANTE_BRAIN_EMBEDDING_MODEL"]="test",
            ["DANTE_BRAIN_EMBEDDING_VERSION"]="1",["DANTE_BRAIN_EMBEDDING_DIMENSION"]="2" }).Build();
        using var ativo=new GeradorDeEmbeddingHttp(new HttpClient(handler),cfg);
        Assert.Null(await ativo.GerarAsync("texto")); handler.Json="{\"data\":[{\"embedding\":[3,4]}]}";
        var v=await ativo.GerarAsync("texto"); Assert.Equal(0.6,v![0],5); Assert.Equal(0.8,v[1],5);
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Chamadas; public string Json="{}";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        { Chamadas++; return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content=new StringContent(Json) }); }
    }
    private sealed class Gerador : IGeradorDeEmbedding
    {
        public string Versao="1"; public bool Indisponivel; public List<string> Textos=[];
        public ModeloEmbedding? Modelo=>new("teste","conceitos-controlados",Versao,3); public bool Externo=>false;
        public Task<float[]?> GerarAsync(string texto,CancellationToken ct=default)
        { Textos.Add(texto); return Task.FromResult<float[]?>(Indisponivel?null:texto.Contains("relação")||texto.Contains("associação")?[1,0,0]:[0,1,0]); }
    }
    private static Conhecimento Novo(Guid e,string texto,Sensibilidade s=Sensibilidade.Trabalho,IReadOnlyList<string>? tags=null,DateTimeOffset? ate=null)=>new(e,null,
        TipoDeConhecimento.Fato,texto,null,StatusDoConhecimento.Inferido,null,s,null,ate,tags??[],new(Guid.NewGuid(),"teste","fonte",null,"trecho"),DateTimeOffset.UtcNow);
    private static ServiceProvider Provider(string connection,IGeradorDeEmbedding gerador)
    {
        var services=new ServiceCollection().AddApplication().AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["ConnectionStrings:Dante"]=connection }).Build());
        services.AddSingleton(gerador); return services.BuildServiceProvider();
    }
}
