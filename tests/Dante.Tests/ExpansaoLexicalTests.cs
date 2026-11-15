using Dante.Application;
using Dante.Application.BuscaDoBrain;
using Dante.Application.DocumentosFonte;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;
using Dante.Domain.DocumentosFonte;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Domain.Projetos;
using Dante.Infrastructure;
using Dante.Infrastructure.BuscaDoBrain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Tests;

public sealed class ExpansaoLexicalTests
{
    [Theory]
    [InlineData("\"faxina lentes\"")]
    [InlineData("faxina or lentes")]
    [InlineData("faxina -lentes")]
    [InlineData("não limpar lentes")]
    [InlineData("limpar lentes sem água")]
    [InlineData("nunca encerrar subprocessos")]
    [InlineData("faxina exceto cozinha")]
    [InlineData("faxina lentes:privadas")]
    [InlineData("faxina")]
    [InlineData("fotossíntese orquídeas")]
    [InlineData("faxina encerrar subprocessos")]
    [InlineData("faxina um1 dois2 tres3 quatro4 cinco5 seis6 sete7 oito8")]
    public void ConsultaIncompativelOuSemExpansaoNaoPerdeRestricoes(string texto) => Assert.Null(ExpansaoLexical.Tentar(texto));

    [Fact]
    public void AncoraCurtaEDesconhecidaPermaneceEmTodasAsAlternativas()
    {
        var consulta = ExpansaoLexical.Tentar("faxina RH abc123");
        Assert.NotNull(consulta);
        Assert.All(consulta.Split(" or "), a => { Assert.Contains("rh", a); Assert.Contains("abc123", a); });
        Assert.All(ExpansaoLexical.Tentar("faxina menos resíduos")!.Split(" or "), a => Assert.Contains("menos", a));
        Assert.Null(ExpansaoLexical.Tentar("faxina " + new string('x',65)));
        Assert.Null(ExpansaoLexical.Tentar("faxina " + string.Join(' ',Enumerable.Repeat(new string('x',63),7).Select((s,i)=>s+i))));
    }

    [PostgreSqlFact]
    public async Task SegundaTentativaReutilizaFiltrosERespeitaPaginacaoEstadoEFontes()
    {
        await using var banco=await Banco.CriarAsync();
        var e=new EspacoDeConhecimento(Guid.NewGuid(),"Expansão");var outro=new EspacoDeConhecimento(e.IdUsuario,"Outro");var projeto=new Projeto(e.Id,"Projeto");
        Conhecimento K(string text,Sensibilidade classe=Sensibilidade.Trabalho,Guid? space=null,Guid? project=null,DateTimeOffset? ate=null,string[]? tags=null)=>
            new(space??e.Id,project,TipoDeConhecimento.Incidente,text,null,StatusDoConhecimento.Inferido,null,classe,null,ate,tags??["teste-147"],
                new(e.IdUsuario,"teste-147","fixture"),DateTimeOffset.UtcNow,NaturezaDoConteudo.ConclusaoDoAgente);
        var a=K("A limpeza dos processos descendentes exige cautela.");var b=K("A limpeza dos processos descendentes exige verificação.");
        var inativo=K("limpeza processos descendentes");inativo.Invalidar(1,inativo.Proveniencia,DateTimeOffset.UtcNow);
        await using(var db=banco.Contexto())
        {
            db.AddRange(e,outro,projeto,a,b,inativo,K("limpeza processos descendentes",Sensibilidade.Secreto),K("limpeza processos descendentes",Sensibilidade.Confidencial),
                K("limpeza processos descendentes",space:outro.Id),K("limpeza processos descendentes",project:projeto.Id),K("limpeza processos descendentes",ate:DateTimeOffset.UtcNow.AddDays(-1)));
            await db.SaveChangesAsync();
        }
        var observed=new List<BuscaDoBrainSearchDto>();
        var cfg=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["ConnectionStrings:Dante"]=banco.ConnectionString}).Build();
        using var app=new ServiceCollection().AddApplication().AddInfrastructure(cfg).AddSingleton<IGeradorDeEmbedding>(new SemEmbedding())
            .AddScoped<IIndiceDeBusca>(s=>new Observado(s.GetRequiredService<IndiceDeBuscaPostgreSql>(),observed)).BuildServiceProvider();
        using var scope=EscoposBrainDeTeste.Criar(app,e.IdUsuario,e.Id);var acesso=new AcessoAoBrain(e.IdUsuario,e.Id,null);
        var busca=scope.ServiceProvider.GetRequiredService<BuscaDoBrainAppService>();
        var filtro=new BuscaDoBrainSearchDto{Texto="limpar processo descendente",Tags=["TESTE-147"],Tipo=TipoDeConhecimento.Incidente,Limite=1};
        var first=await busca.BuscarAsync(acesso,filtro);
        Assert.Equal("lexical",first.Modo);Assert.Null(first.ModeloEmbedding);Assert.True(first.TemMais);
        Assert.StartsWith("Segunda tentativa lexical",Assert.Single(first.Resultados).Explicacao);
        Assert.Equal(2,observed.Count);Assert.Equal(observed[0] with{Texto=observed[1].Texto},observed[1]);
        var second=await busca.BuscarAsync(acesso,filtro with{Deslocamento=1});Assert.False(second.TemMais);
        Assert.NotEqual(first.Resultados[0].Item.Id,Assert.Single(second.Resultados).Item.Id);
        Assert.Empty((await busca.BuscarAsync(acesso,filtro with{Deslocamento=2})).Resultados);
        foreach(var denied in new[]{filtro with{Tags=["ausente"]},filtro with{Status=StatusDoConhecimento.Confirmado},
            filtro with{NaturezaDeOrigem=NaturezaDoConteudo.DitoPeloUsuario},filtro with{CriadoDesde=DateTimeOffset.UtcNow.AddDays(1)}})
            Assert.Empty((await busca.BuscarAsync(acesso,denied)).Resultados);
        observed.Clear();await busca.BuscarAsync(acesso,new(){Texto="limpar processo descendente",TextoIntegral="limpar processo descendente"});Assert.Single(observed);
        observed.Clear();await busca.BuscarAsync(acesso,new(){Texto="limpar processo descendente",IdConhecimento=a.Id});Assert.Single(observed);
        await scope.ServiceProvider.GetRequiredService<DocumentoFonteAppService>().ImportarAsync(acesso,"nota:expansao","markdown","A higienização das lentes do telescópio exige cautela.",AutoriaDaFonte.Agente);
        var fonte=Assert.Single((await busca.BuscarAsync(acesso,new(){Texto="faxina lentes"})).Resultados);
        Assert.Equal(OrigemDoResultado.FonteBruta,fonte.Origem);Assert.StartsWith("Segunda tentativa lexical",fonte.Explicacao);
        Assert.Empty((await busca.BuscarAsync(acesso,new(){Texto="faxina lentes",Tipo=TipoDeConhecimento.Referencia})).Resultados);
        observed.Clear();var literal=await busca.BuscarAsync(acesso,new(){Texto="limpeza processos descendentes",Deslocamento=50});
        Assert.Empty(literal.Resultados);Assert.Single(observed); // Não trocar de interpretação só porque a página acabou.
        await using(var db=banco.Contexto()){Assert.All(db.Conhecimentos.Where(x=>x.Id==a.Id||x.Id==b.Id),k=>Assert.Equal(1,k.Revisao));}
    }

    private sealed class SemEmbedding:IGeradorDeEmbedding
    {public ModeloEmbedding? Modelo=>null;public bool Externo=>false;public double? SimilaridadeMinima=>null;public Task<float[]?> GerarAsync(string texto,CancellationToken ct=default)=>throw new InvalidOperationException("Sem embeddings neste ensaio");}
    private sealed class Observado(IndiceDeBuscaPostgreSql real,List<BuscaDoBrainSearchDto> chamadas):IIndiceDeBusca
    {
        public Task<bool> VetoresDisponiveisAsync(CancellationToken ct=default)=>real.VetoresDisponiveisAsync(ct);
        public Task<IReadOnlyList<MatchDaBusca>> BuscarAsync(AcessoAoBrain a,BuscaDoBrainSearchDto f,ModeloEmbedding? m,float[]? v,double s=1,CancellationToken ct=default)
        {chamadas.Add(f);return real.BuscarAsync(a,f,m,v,s,ct);}
        public Task<IReadOnlyList<Guid>> ListarPendentesAsync(AcessoAoBrain a,ModeloEmbedding m,int l,CancellationToken ct=default)=>real.ListarPendentesAsync(a,m,l,ct);
        public Task<bool> GravarAsync(Guid id,int rev,ModeloEmbedding m,float[] v,CancellationToken ct=default)=>real.GravarAsync(id,rev,m,v,ct);
        public Task LimparModeloAsync(AcessoAoBrain a,ModeloEmbedding m,CancellationToken ct=default)=>real.LimparModeloAsync(a,m,ct);
    }
}
