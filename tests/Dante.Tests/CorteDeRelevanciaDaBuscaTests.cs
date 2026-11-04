using Dante.Application;
using Dante.Application.BuscaDoBrain;
using Dante.Application.ConstrucaoDeContexto;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Domain.RelacoesDeConhecimento;
using Dante.Infrastructure;
using Dante.Infrastructure.BuscaDoBrain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
namespace Dante.Tests;

// #280: corte de relevância da busca híbrida, com vetores controlados (cossenos exatos) e limiar 0,5.
public sealed class CorteDeRelevanciaDaBuscaTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("abc", null)]
    [InlineData("0", null)]
    [InlineData("1", null)]
    [InlineData("-0.2", null)]
    [InlineData("1.5", null)]
    [InlineData("NaN", null)]
    [InlineData("0,52", null)]
    [InlineData("0.52", 0.52)]
    public void SimilaridadeMinimaSoValeNaFaixaAbertaEmCulturaInvariante(string? valor, double? esperado)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DANTE_BRAIN_EMBEDDING_MIN_SIMILARITY"] = valor }).Build();
        using var gerador = new GeradorDeEmbeddingHttp(new HttpClient(), cfg);
        Assert.Equal(esperado, gerador.SimilaridadeMinima);
    }

    [PostgreSqlFact]
    public async Task CorteDescartaFracosPreservaLiteralEAbstemSemResposta()
    {
        await using var cenario = await Cenario.CriarAsync();
        var busca = cenario.Servico<BuscaDoBrainAppService>();

        var semantica = await busca.BuscarAsync(cenario.Acesso, new() { Texto = "alfa consulta" });
        Assert.Equal("hibrido", semantica.Modo);
        // Só os de cosseno 0,9 passam; os de 0,3 (Distante e Literal sem correspondência lexical) são descartados.
        Assert.Equal(new[] { cenario.Proximo.Id, cenario.Apoiado.Id }.Order(), semantica.Resultados.Select(x => x.Item.Id).Order());
        Assert.All(semantica.Resultados, x => Assert.Equal(0.9, x.ScoreSemantico!.Value, 3));

        // A correspondência lexical entra mesmo com semântica abaixo do limiar.
        var literal = await busca.BuscarAsync(cenario.Acesso, new() { Texto = "zebra listrada" });
        Assert.Equal(cenario.Literal.Id, Assert.Single(literal.Resultados).Item.Id);

        // Nada lexical e nada acima do limiar: abstém em vez de completar a página.
        Assert.Empty((await busca.BuscarAsync(cenario.Acesso, new() { Texto = "nada aqui" })).Resultados);
        // Secreto não é vetorizado nem retornado, mesmo próximo da consulta.
        Assert.DoesNotContain((await busca.BuscarAsync(cenario.Acesso, new() { Texto = "alfa", Limite = 100 })).Resultados, x => x.Item.Id == cenario.Secreto.Id);
    }

    [PostgreSqlFact]
    public async Task LexicalParcialExigeApoioSemanticoSoNaBuscaHibrida()
    {
        await using var cenario = await Cenario.CriarAsync();
        var busca = cenario.Servico<BuscaDoBrainAppService>();
        // "zebra or consulta" casa só "zebra"; a mensagem inteira não casa e o vetor dos termos não apoia.
        Assert.Empty((await busca.BuscarAsync(cenario.Acesso, new() { Texto = "zebra or consulta", TextoIntegral = "zebra consulta" })).Resultados);
        // Todos os termos da mensagem casam: lexical preservado.
        Assert.Contains((await busca.BuscarAsync(cenario.Acesso, new() { Texto = "zebra or listrada", TextoIntegral = "zebra listrada" })).Resultados,
            x => x.Item.Id == cenario.Literal.Id);
        // Parcial com apoio semântico acima do limiar entra.
        var apoiado = await busca.BuscarAsync(cenario.Acesso, new() { Texto = "zebra or alfa", TextoIntegral = "zebra alfa" });
        Assert.Contains(apoiado.Resultados, x => x.Item.Id == cenario.Apoiado.Id);
        Assert.DoesNotContain(apoiado.Resultados, x => x.Item.Id == cenario.Literal.Id);
        // Sem TextoIntegral (canal agente), a semântica OR da consulta é a de sempre.
        Assert.Contains((await busca.BuscarAsync(cenario.Acesso, new() { Texto = "zebra or consulta" })).Resultados, x => x.Item.Id == cenario.Literal.Id);
    }

    [PostgreSqlFact]
    public async Task SemLimiarCalibradoBuscaFicaLexicalSemGerarEmbeddingDaConsulta()
    {
        await using var cenario = await Cenario.CriarAsync();
        cenario.Gerador.SimilaridadeMinima = null; var antes = cenario.Gerador.Textos.Count;
        var busca = cenario.Servico<BuscaDoBrainAppService>();
        var r = await busca.BuscarAsync(cenario.Acesso, new() { Texto = "zebra or consulta", TextoIntegral = "zebra consulta" });
        Assert.Equal("lexical", r.Modo); Assert.Null(r.ModeloEmbedding);
        // No modo lexical, TextoIntegral não muda nada: a correspondência parcial continua.
        Assert.Contains(r.Resultados, x => x.Item.Id == cenario.Literal.Id);
        Assert.Empty((await busca.BuscarAsync(cenario.Acesso, new() { Texto = "alfa consulta" })).Resultados);
        Assert.Equal(antes, cenario.Gerador.Textos.Count);
        // A indexação não depende do limiar: só a consulta semântica fica bloqueada.
        cenario.Gerador.SimilaridadeMinima = 0.5;
        Assert.Equal("hibrido", (await busca.BuscarAsync(cenario.Acesso, new() { Texto = "alfa consulta" })).Modo);
    }

    [PostgreSqlFact]
    public async Task PacoteAutomaticoNaoCompletaComFracosEExpandeSoAprovados()
    {
        await using var cenario = await Cenario.CriarAsync();
        var construtor = cenario.Servico<ConstrutorDeContextoAppService>();
        var pacote = await construtor.ConstruirAsync(cenario.Acesso, new()
            { Mensagem = "alfa consulta", Filtros = new() { Texto = "alfa or consulta" }, ProfundidadeDeRelacoes = 1, IncluirSnapshot = false });
        var ids = pacote.Itens.Select(x => x.Id).ToHashSet();
        Assert.Contains(cenario.Proximo.Id, ids);
        Assert.Contains(cenario.VizinhoDoProximo.Id, ids);       // relação a partir de um aprovado
        Assert.DoesNotContain(cenario.Distante.Id, ids);          // abaixo do limiar
        Assert.DoesNotContain(cenario.VizinhoDoDistante.Id, ids); // relação a partir de um cortado
        Assert.DoesNotContain(cenario.Secreto.Id, ids);

        var vazio = await construtor.ConstruirAsync(cenario.Acesso, new()
            { Mensagem = "zebra consulta", Filtros = new() { Texto = "zebra or consulta" }, IncluirSnapshot = false });
        Assert.Empty(vazio.Itens); Assert.Equal("", vazio.TextoParaInjecao);
    }

    // Vetor pela palavra-chave do texto: cosseno com "alfa" = 1 (alfa), 0,9 (quase), 0,3 (longe), 0 (beta e demais).
    private sealed class Gerador : IGeradorDeEmbedding
    {
        public List<string> Textos { get; } = [];
        public double? SimilaridadeMinima { get; set; } = 0.5;
        public ModeloEmbedding? Modelo => new("teste", "corte", "1", 3);
        public bool Externo => false;
        public Task<float[]?> GerarAsync(string texto, CancellationToken cancellationToken = default)
        {
            Textos.Add(texto);
            float[] v = texto.Contains("alfa") ? [1, 0, 0] : texto.Contains("quase") ? [0.9f, 0.43589f, 0] :
                texto.Contains("longe") ? [0.3f, 0.95394f, 0] : texto.Contains("beta") ? [0, 1, 0] : [0, 0, 1];
            return Task.FromResult<float[]?>(v);
        }
    }

    private sealed class Cenario : IAsyncDisposable
    {
        private Banco banco = null!; private ServiceProvider provider = null!; private IServiceScope scope = null!;
        public Gerador Gerador { get; } = new();
        public AcessoAoBrain Acesso { get; private set; } = null!;
        public Conhecimento Proximo { get; private set; } = null!;
        public Conhecimento Distante { get; private set; } = null!;
        public Conhecimento Literal { get; private set; } = null!;
        public Conhecimento Apoiado { get; private set; } = null!;
        public Conhecimento Secreto { get; private set; } = null!;
        public Conhecimento VizinhoDoProximo { get; private set; } = null!;
        public Conhecimento VizinhoDoDistante { get; private set; } = null!;
        public T Servico<T>() where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

        public static async Task<Cenario> CriarAsync()
        {
            var c = new Cenario { banco = await Banco.CriarAsync() };
            var e = new EspacoDeConhecimento(Guid.NewGuid(), "Corte #280");
            c.Proximo = Novo(e.Id, "registro quase igual"); c.Distante = Novo(e.Id, "registro longe");
            c.Literal = Novo(e.Id, "zebra listrada longe"); c.Apoiado = Novo(e.Id, "zebra quase");
            c.Secreto = Novo(e.Id, "segredo quase alfa", Sensibilidade.Secreto);
            c.VizinhoDoProximo = Novo(e.Id, "beta vizinho aprovado"); c.VizinhoDoDistante = Novo(e.Id, "beta vizinho cortado");
            var relacoes = new[]
            {
                new RelacaoDeConhecimento(c.Proximo, c.VizinhoDoProximo, TipoDeRelacao.RelacionadoA, c.Proximo.Proveniencia, DateTimeOffset.UtcNow),
                new RelacaoDeConhecimento(c.Distante, c.VizinhoDoDistante, TipoDeRelacao.RelacionadoA, c.Distante.Proveniencia, DateTimeOffset.UtcNow),
            };
            await using (var db = c.banco.Contexto())
            {
                db.AddRange(e, c.Proximo, c.Distante, c.Literal, c.Apoiado, c.Secreto, c.VizinhoDoProximo, c.VizinhoDoDistante); db.AddRange(relacoes);
                await db.SaveChangesAsync();
            }
            var services = new ServiceCollection().AddApplication().AddInfrastructure(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Dante"] = c.banco.ConnectionString }).Build());
            services.AddSingleton<IGeradorDeEmbedding>(c.Gerador); c.provider = services.BuildServiceProvider();
            c.Acesso = new AcessoAoBrain(e.IdUsuario, e.Id, null);
            c.scope = EscoposBrainDeTeste.Criar(c.provider, e.IdUsuario, e.Id);
            await c.Servico<IndiceDeBuscaPostgreSql>().PrepararVetoresAsync();
            Assert.Equal(6, await c.Servico<BuscaDoBrainAppService>().ReindexarAsync(c.Acesso));
            Assert.DoesNotContain(c.Gerador.Textos, x => x.Contains("segredo"));
            return c;
        }

        private static Conhecimento Novo(Guid espaco, string texto, Sensibilidade s = Sensibilidade.Trabalho) => new(espaco, null, TipoDeConhecimento.Fato, texto, null,
            StatusDoConhecimento.Inferido, null, s, null, null, [], new(Guid.NewGuid(), "teste", "fonte", null, "trecho"), DateTimeOffset.UtcNow, NaturezaDoConteudo.DitoPeloUsuario);

        public async ValueTask DisposeAsync() { scope.Dispose(); await provider.DisposeAsync(); await banco.DisposeAsync(); }
    }
}
