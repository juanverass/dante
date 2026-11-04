using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dante.Application;
using Dante.Application.BuscaDoBrain;
using Dante.Application.ConstrucaoDeContexto;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Infrastructure;
using Dante.Infrastructure.BuscaDoBrain;
using Dante.Worker.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit.Abstractions;
namespace Dante.Tests;

// Medição opt-in da #258/#280: o mesmo corpus em modo lexical e híbrido, com e sem corte de relevância.
// Qualidade não é asserção (depende do modelo); invariantes de segurança e de acerto literal são.
// Com DANTE_TEST_BUSCA_RELATORIO, grava busca-<nome>.json nesse diretório para comparação.
// A calibração (#280) usa só busca-brain.json; busca-brain-validacao.json fica para a validação final.
public sealed class ConsultasDeReferenciaDaBuscaTests(ITestOutputHelper saida)
{
    [PostgreSqlFact]
    public Task LinhaDeBaseLexical() => MedirAsync(Conjunto.Calibracao(), "lexical", null);

    [EmbeddingLocalFact]
    public Task BuscaHibridaComEmbeddingLocal() => MedirAsync(Conjunto.Calibracao(), "hibrido", Embedding());

    // Scores lexical e semântico de todos os itens indexáveis, só para as consultas de calibração.
    [EmbeddingLocalFact]
    public Task MatrizDeCalibracao() => GravarMatrizAsync(Conjunto.Calibracao());

    private static Dictionary<string, string?> Embedding(IReadOnlyDictionary<string, string?>? extra = null)
    {
        var d = new Dictionary<string, string?>
        {
            ["DANTE_BRAIN_EMBEDDING_ENDPOINT"] = Environment.GetEnvironmentVariable("DANTE_TEST_EMBEDDING_ENDPOINT"),
            ["DANTE_BRAIN_EMBEDDING_MODEL"] = Environment.GetEnvironmentVariable("DANTE_TEST_EMBEDDING_MODEL"),
            ["DANTE_BRAIN_EMBEDDING_VERSION"] = Environment.GetEnvironmentVariable("DANTE_TEST_EMBEDDING_VERSION"),
            ["DANTE_BRAIN_EMBEDDING_DIMENSION"] = Environment.GetEnvironmentVariable("DANTE_TEST_EMBEDDING_DIMENSION"),
            ["DANTE_BRAIN_EMBEDDING_PROVIDER"] = Environment.GetEnvironmentVariable("DANTE_TEST_EMBEDDING_PROVIDER") ?? "local",
        };
        foreach (var (k, v) in extra ?? new Dictionary<string, string?>()) d[k] = v;
        return d;
    }

    private sealed record Ambiente(Banco Banco, ServiceProvider Provider, EspacoDeConhecimento Espaco, Dictionary<Guid, string> Chaves,
        GeradorCronometrado Gerador, AcessoAoBrain Acesso, object? Indexacao, double? AquecimentoMs) : IAsyncDisposable
    {
        public IServiceScope Escopo() => EscoposBrainDeTeste.Criar(Provider, Espaco.IdUsuario, Espaco.Id);
        public async ValueTask DisposeAsync() { await Provider.DisposeAsync(); await Banco.DisposeAsync(); }
    }

    // Banco descartável com o corpus versionado; com embedding, indexa e aquece antes de medir.
    private static async Task<Ambiente> PrepararAsync(Conjunto conjunto, Dictionary<string, string?>? embedding)
    {
        var banco = await Banco.CriarAsync();
        var espaco = new EspacoDeConhecimento(Guid.NewGuid(), "Referência #258");
        var chaves = new Dictionary<Guid, string>();
        await using (var c = banco.Contexto())
        {
            c.Add(espaco);
            foreach (var item in conjunto.Corpus)
            {
                var origem = new ProvenienciaDoConhecimento(Guid.NewGuid(), "referencia-258", "busca-brain.json", null, item.Chave);
                var k = new Conhecimento(espaco.Id, null, Enum.Parse<TipoDeConhecimento>(item.Tipo), item.Conteudo, null, StatusDoConhecimento.Inferido, null,
                    Enum.Parse<Sensibilidade>(item.Sensibilidade ?? "Trabalho"), null, null, item.Tags, origem, DateTimeOffset.UtcNow, NaturezaDoConteudo.DitoPeloUsuario);
                if (item.Confirmado) k.Confirmar(1, origem, DateTimeOffset.UtcNow);
                if (item.Inativo) k.Invalidar(k.Revisao, origem, DateTimeOffset.UtcNow);
                c.Add(k); chaves[k.Id] = item.Chave;
            }
            await c.SaveChangesAsync();
        }
        var configuracao = new Dictionary<string, string?>(embedding ?? []) { ["ConnectionStrings:Dante"] = banco.ConnectionString };
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(configuracao).Build();
        var gerador = new GeradorCronometrado(new GeradorDeEmbeddingHttp(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }), cfg));
        var services = new ServiceCollection().AddApplication().AddInfrastructure(cfg);
        services.AddSingleton<IGeradorDeEmbedding>(gerador);
        var provider = services.BuildServiceProvider();
        var acesso = new AcessoAoBrain(espaco.IdUsuario, espaco.Id, null);
        var proibidos = conjunto.Proibidos.ToHashSet(StringComparer.Ordinal);
        object? indexacao = null; double? aquecimentoMs = null;

        if (embedding is not null)
        {
            // Garantia da #258: conteúdo Trabalho/Pessoal só vai para endpoint de loopback.
            Assert.NotNull(gerador.Modelo); Assert.False(gerador.Externo, "Endpoint de embedding precisa ser loopback.");
            var relogio = Stopwatch.StartNew(); int gravados = 0, rodada;
            using (var scope = EscoposBrainDeTeste.Criar(provider, espaco.IdUsuario, espaco.Id))
            {
                var indice = scope.ServiceProvider.GetRequiredService<IndiceDeBuscaPostgreSql>();
                await indice.PrepararVetoresAsync(); await indice.PrepararVetoresAsync();
            }
            do
            {
                using var scope = EscoposBrainDeTeste.Criar(provider, espaco.IdUsuario, espaco.Id);
                rodada = await scope.ServiceProvider.GetRequiredService<BuscaDoBrainAppService>().ReindexarAsync(acesso);
                gravados += rodada;
            } while (rodada > 0);
            relogio.Stop();
            await using var c = banco.Contexto();
            var indexados = await c.Database.SqlQuery<Guid>($"SELECT id_conhecimento AS \"Value\" FROM brain_index.representacoes WHERE vetor IS NOT NULL").ToListAsync();
            // Secreto, Confidencial (sem permissão) e inativo nunca recebem vetor.
            Assert.DoesNotContain(indexados, id => proibidos.Contains(chaves[id]));
            Assert.Equal(conjunto.Corpus.Count - proibidos.Count, gravados);
            indexacao = new { gravados, milissegundos = relogio.Elapsed.TotalMilliseconds, chamadas = gerador.Chamadas.Count, msPorEmbedding = Media(gerador.Chamadas) };
            gerador.Chamadas.Clear();
            // Aquecimento: a primeira consulta pode incluir carga do modelo; fica registrada à parte.
            relogio.Restart();
            using (var scope = EscoposBrainDeTeste.Criar(provider, espaco.IdUsuario, espaco.Id))
                await scope.ServiceProvider.GetRequiredService<BuscaDoBrainAppService>().BuscarAsync(acesso, new() { Texto = "aquecimento" });
            aquecimentoMs = relogio.Elapsed.TotalMilliseconds;
            gerador.Chamadas.Clear();
        }
        return new(banco, provider, espaco, chaves, gerador, acesso, indexacao, aquecimentoMs);
    }

    // varianteA: o canal automático gera o embedding da mensagem natural (#280). Sem ela, emula o
    // comportamento anterior, em que o vetor vinha dos termos unidos por "or".
    private async Task MedirAsync(Conjunto conjunto, string nome, Dictionary<string, string?>? embedding, bool varianteA = true)
    {
        await using var amb = await PrepararAsync(conjunto, embedding);
        var proibidos = conjunto.Proibidos.ToHashSet(StringComparer.Ordinal);
        var esperado = embedding is null ? "lexical" : "hibrido";
        var resultados = new List<ResultadoDaConsulta>();
        foreach (var q in conjunto.Consultas)
        {
            using var scope = amb.Escopo();
            var busca = scope.ServiceProvider.GetRequiredService<BuscaDoBrainAppService>();
            var antes = amb.Gerador.Chamadas.Count; var relogio = Stopwatch.StartNew();
            // Canal agente (MCP): texto cru, limite padrão da ferramenta.
            var r = await busca.BuscarAsync(amb.Acesso, new() { Texto = q.Texto, Tags = q.Tags ?? [], Limite = 10 });
            var msAgente = relogio.Elapsed.TotalMilliseconds;
            var embAgente = amb.Gerador.Chamadas.Skip(antes).Sum();
            Assert.Equal(esperado, r.Modo);
            var ids = r.Resultados.Select(x => amb.Chaves[x.Item.Id]).ToArray();
            // Canal automático (Telegram): termos unidos por OR e parâmetros do pacote inicial.
            var termos = TelegramBrain.Termos(q.Texto); antes = amb.Gerador.Chamadas.Count; relogio.Restart();
            var pacote = await scope.ServiceProvider.GetRequiredService<ConstrutorDeContextoAppService>().ConstruirAsync(amb.Acesso,
                new() { Mensagem = varianteA ? q.Texto : termos, Filtros = new() { Texto = termos }, OrcamentoDeTokens = 2048, LimiteDeItens = 12, IncluirSnapshot = false });
            var msAutomatico = relogio.Elapsed.TotalMilliseconds;
            var embAutomatico = amb.Gerador.Chamadas.Skip(antes).Sum();
            var selecionados = pacote.Itens.Select(x => amb.Chaves[x.Id]).ToArray();
            Assert.DoesNotContain(ids, proibidos.Contains); Assert.DoesNotContain(selecionados, proibidos.Contains);
            Assert.DoesNotContain(pacote.Registros, x => amb.Chaves.TryGetValue(x.Id, out var k) && proibidos.Contains(k) && x.Estado != "descartado");
            resultados.Add(new(q, termos, ids, r.Resultados.Select(x => new Pontuacao(amb.Chaves[x.Item.Id], x.Score, x.ScoreLexical, x.ScoreSemantico)).ToArray(),
                msAgente, embAgente, selecionados, pacote.Registros.Select(x => x.Chave).Distinct(StringComparer.Ordinal).Count(),
                pacote.Registros.Count(x => x.Estado == "descartado"), pacote.Custo.TokensEstimados, msAutomatico, embAutomatico));
        }
        // Na calibração, o híbrido não pode perder o que a busca literal já acha; na validação, o resultado é só relatado.
        if (conjunto.Nome == "calibracao")
            Assert.All(resultados.Where(x => x.Consulta.Categoria == "literal"), x => Assert.True(x.AcertoEm(5), $"{x.Consulta.Id} perdeu acerto literal."));

        var cfg = embedding ?? [];
        var relatorio = new
        {
            conjunto = $"{conjunto.Nome}@{conjunto.Versao}", modo = esperado, nome, geradoEm = DateTimeOffset.UtcNow,
            modelo = amb.Gerador.Modelo is { } m ? $"{m.Provedor}/{m.Nome}@{m.Versao} ({m.Dimensao})" : null,
            corte = embedding is null ? null : new
            {
                similaridadeMinima = cfg.GetValueOrDefault("DANTE_BRAIN_EMBEDDING_MIN_SIMILARITY"),
                margemMaxima = cfg.GetValueOrDefault("DANTE_BRAIN_EMBEDDING_MAX_GAP"),
                varianteA,
            },
            amb.Indexacao, amb.AquecimentoMs, geral = Agregar(resultados), porCategoria = resultados.GroupBy(x => x.Consulta.Categoria).ToDictionary(g => g.Key, g => Agregar(g.ToList())),
            consultas = resultados.Select(x => new
            {
                x.Consulta.Id, x.Consulta.Categoria, x.Consulta.Texto, x.Consulta.Relevantes,
                agente = new { recuperados = x.Ids, pontuacoes = x.Pontuacoes, acerto1 = x.AcertoEm(1), acerto5 = x.AcertoEm(5), rr = x.Rr(), revocacao5 = x.Revocacao5(), ms = x.MsAgente, msEmbedding = x.EmbAgente },
                automatico = new { x.Termos, x.Recuperados, selecionados = x.Selecionados, x.Descartados, tokens = x.Tokens, relevantesSelecionados = x.RelevantesSelecionados(), ms = x.MsAutomatico, msEmbedding = x.EmbAutomatico },
            }),
        };
        saida.WriteLine(JsonSerializer.Serialize(new { relatorio.nome, relatorio.modelo, relatorio.corte, relatorio.Indexacao, relatorio.AquecimentoMs, relatorio.geral, relatorio.porCategoria }, Json));
        await GravarAsync($"busca-{nome}.json", relatorio);
    }

    private async Task GravarMatrizAsync(Conjunto conjunto)
    {
        Assert.Equal("calibracao", conjunto.Nome);
        await using var amb = await PrepararAsync(conjunto, Embedding());
        var consultas = new List<object>();
        await using var conexao = new NpgsqlConnection(amb.Banco.ConnectionString); await conexao.OpenAsync();
        foreach (var q in conjunto.Consultas)
        {
            var termos = TelegramBrain.Termos(q.Texto);
            var cru = await amb.Gerador.GerarAsync(q.Texto); var deTermos = await amb.Gerador.GerarAsync(termos);
            Assert.NotNull(cru); Assert.NotNull(deTermos);
            await using var c = new NpgsqlCommand("""
                SELECT c.id,
                    COALESCE(ts_rank_cd(t.documento, websearch_to_tsquery('portuguese', @cru)), 0)::double precision,
                    COALESCE(ts_rank_cd(t.documento, websearch_to_tsquery('portuguese', @termos)), 0)::double precision,
                    1 - (r.vetor <=> CAST(@vcru AS vector)), 1 - (r.vetor <=> CAST(@vtermos AS vector))
                FROM brain_data.conhecimentos c JOIN brain_index.trabalhos t ON t.id_conhecimento = c.id
                JOIN brain_index.representacoes r ON r.id_conhecimento = c.id AND r.revisao = t.revisao AND r.vetor IS NOT NULL
                """, conexao);
            c.Parameters.AddWithValue("cru", q.Texto); c.Parameters.AddWithValue("termos", termos);
            c.Parameters.AddWithValue("vcru", Vetor(BuscaDoBrainAppService.Normalizar(cru!, cru!.Length)));
            c.Parameters.AddWithValue("vtermos", Vetor(BuscaDoBrainAppService.Normalizar(deTermos!, deTermos!.Length)));
            var itens = new List<object>();
            await using (var leitor = await c.ExecuteReaderAsync())
                while (await leitor.ReadAsync())
                    itens.Add(new { chave = amb.Chaves[leitor.GetGuid(0)], lexicalCru = leitor.GetDouble(1), lexicalTermos = leitor.GetDouble(2), semanticoCru = leitor.GetDouble(3), semanticoTermos = leitor.GetDouble(4) });
            consultas.Add(new { q.Id, q.Categoria, q.Texto, termos, q.Relevantes, itens });
        }
        var m = amb.Gerador.Modelo!;
        await GravarAsync("calibracao-matriz.json", new { conjunto = $"{conjunto.Nome}@{conjunto.Versao}", modelo = $"{m.Provedor}/{m.Nome}@{m.Versao} ({m.Dimensao})", geradoEm = DateTimeOffset.UtcNow, consultas });
    }

    private static string Vetor(float[] v) => "[" + string.Join(",", v.Select(x => x.ToString("R", CultureInfo.InvariantCulture))) + "]";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static async Task GravarAsync(string arquivo, object conteudo)
    {
        if (Environment.GetEnvironmentVariable("DANTE_TEST_BUSCA_RELATORIO") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, arquivo), JsonSerializer.Serialize(conteudo, Json) + "\n");
    }

    private static object Agregar(IReadOnlyList<ResultadoDaConsulta> r)
    {
        var comResposta = r.Where(x => x.Consulta.Relevantes.Count > 0).ToList(); var semResposta = r.Where(x => x.Consulta.Relevantes.Count == 0).ToList();
        double Taxa(Func<ResultadoDaConsulta, bool> f) => comResposta.Count == 0 ? 0 : Math.Round(comResposta.Count(f) / (double)comResposta.Count, 3);
        double Med(IEnumerable<double> v) => v.Any() ? Math.Round(v.Average(), 3) : 0;
        var selecionados = r.Sum(x => x.Selecionados.Length);
        return new
        {
            consultas = r.Count, comResposta = comResposta.Count,
            acerto1 = Taxa(x => x.AcertoEm(1)), acerto5 = Taxa(x => x.AcertoEm(5)), mrr = Med(comResposta.Select(x => x.Rr())), revocacao5 = Med(comResposta.Select(x => x.Revocacao5())),
            pacoteComRelevante = Taxa(x => x.RelevantesSelecionados() > 0),
            precisaoDoPacote = selecionados == 0 ? 0 : Math.Round(r.Sum(x => x.RelevantesSelecionados()) / (double)selecionados, 3),
            semRespostaComResultado = semResposta.Count(x => x.Ids.Length > 0), semRespostaComPacote = semResposta.Count(x => x.Selecionados.Length > 0),
            recuperadosMedio = Med(r.Select(x => (double)x.Recuperados)), selecionadosMedio = Med(r.Select(x => (double)x.Selecionados.Length)),
            descartadosMedio = Med(r.Select(x => (double)x.Descartados)), tokensMedio = Med(r.Select(x => (double)x.Tokens)), tokensTotal = r.Sum(x => x.Tokens),
            msAgenteP50 = Percentil(r.Select(x => x.MsAgente), 0.5), msAgenteP95 = Percentil(r.Select(x => x.MsAgente), 0.95),
            msAutomaticoP50 = Percentil(r.Select(x => x.MsAutomatico), 0.5), msAutomaticoP95 = Percentil(r.Select(x => x.MsAutomatico), 0.95),
            msEmbeddingMedio = Med(r.Select(x => x.EmbAgente).Concat(r.Select(x => x.EmbAutomatico)).Where(x => x > 0)),
        };
    }
    private static double Media(IReadOnlyCollection<double> v) => v.Count == 0 ? 0 : Math.Round(v.Average(), 3);
    private static double Percentil(IEnumerable<double> valores, double p)
    {
        var v = valores.OrderBy(x => x).ToArray(); if (v.Length == 0) return 0;
        return Math.Round(v[Math.Min(v.Length - 1, (int)Math.Ceiling(p * v.Length) - 1)], 3);
    }

    private sealed record Pontuacao(string Chave, double Score, double Lexical, double? Semantico);
    private sealed record ResultadoDaConsulta(Consulta Consulta, string Termos, string[] Ids, Pontuacao[] Pontuacoes, double MsAgente, double EmbAgente,
        string[] Selecionados, int Recuperados, int Descartados, int Tokens, double MsAutomatico, double EmbAutomatico)
    {
        public bool AcertoEm(int k) => Ids.Take(k).Any(Consulta.Relevantes.Contains);
        public double Rr() { var i = Array.FindIndex(Ids, Consulta.Relevantes.Contains); return i < 0 ? 0 : 1.0 / (i + 1); }
        public double Revocacao5() => Consulta.Relevantes.Count == 0 ? 0 : Ids.Take(5).Count(Consulta.Relevantes.Contains) / (double)Consulta.Relevantes.Count;
        public int RelevantesSelecionados() => Selecionados.Count(Consulta.Relevantes.Contains);
    }
    internal sealed record ItemDoCorpus(string Chave, string Tipo, string Conteudo, IReadOnlyList<string> Tags, string? Sensibilidade, bool Confirmado, bool Inativo);
    internal sealed record Consulta(string Id, string Categoria, string Texto, IReadOnlyList<string> Relevantes, IReadOnlyList<string>? Tags, string? Origem);
    internal sealed record Conjunto(int Versao, IReadOnlyList<string> Proibidos, IReadOnlyList<ItemDoCorpus> Corpus, IReadOnlyList<Consulta> Consultas)
    {
        public string Nome { get; init; } = "calibracao";
        private static readonly JsonSerializerOptions Opcoes = new() { PropertyNameCaseInsensitive = true };
        private static string Ler(string arquivo) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ConsultasDeReferencia", arquivo));
        public static Conjunto Calibracao() => JsonSerializer.Deserialize<Conjunto>(Ler("busca-brain.json"), Opcoes)!;
        // Mesmo corpus da calibração; só as consultas vêm do conjunto de validação.
        public static Conjunto Validacao()
        {
            var validacao = JsonSerializer.Deserialize<ConsultasDeValidacao>(Ler("busca-brain-validacao.json"), Opcoes)!;
            return Calibracao() with { Versao = validacao.Versao, Consultas = validacao.Consultas, Nome = "validacao" };
        }
    }
    internal sealed record ConsultasDeValidacao(int Versao, IReadOnlyList<Consulta> Consultas);
    // Mede cada chamada ao gerador real sem alterar o resultado.
    private sealed class GeradorCronometrado(GeradorDeEmbeddingHttp interno) : IGeradorDeEmbedding
    {
        public List<double> Chamadas { get; } = [];
        public ModeloEmbedding? Modelo => interno.Modelo;
        public bool Externo => interno.Externo;
        public async Task<float[]?> GerarAsync(string texto, CancellationToken cancellationToken = default)
        {
            var relogio = Stopwatch.StartNew();
            try { return await interno.GerarAsync(texto, cancellationToken); }
            finally { lock (Chamadas) Chamadas.Add(relogio.Elapsed.TotalMilliseconds); }
        }
    }
}

public sealed class EmbeddingLocalFactAttribute : FactAttribute
{
    public EmbeddingLocalFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DANTE_TEST_POSTGRES")) ||
            new[] { "ENDPOINT", "MODEL", "VERSION", "DIMENSION" }.Any(x => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DANTE_TEST_EMBEDDING_" + x))))
            Skip = "Defina DANTE_TEST_POSTGRES (com pgvector) e DANTE_TEST_EMBEDDING_ENDPOINT/MODEL/VERSION/DIMENSION de um provedor local.";
    }
}
