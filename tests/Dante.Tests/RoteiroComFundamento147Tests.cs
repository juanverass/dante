using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dante.Domain.ContextosDeTrabalho;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Dante.Tests;

// Roteiro issue147-cenarios@2 (captura por fundamento) e consolidação condicional da fonte, sem CLI real e sem medição.
// A fixture é pré-registrada (hash fixo, publicado na #147) e preserva os casos, consultas, durações e essenciais do @1.
// A montagem offline aplica os pedidos do @2 como um agente que os cumpre à letra (citações pré-registradas, conteúdo =
// linhas citadas), pelos serviços de produção (MCP para evidência, captura, vínculo e snapshot; confirmação pela
// Application), e reconstrói o pacote pela mesma chamada da sessão B. Bytes e tokens aqui não são medições.
public sealed class RoteiroComFundamento147Tests(ITestOutputHelper output)
{
    // Publicado na #147 antes de qualquer execução. Mudar a fixture exige novo pré-registro, nunca ajuste silencioso.
    internal const string HashDaFixtureComFundamento = "e4161af239b8d0f707f48876dcbf6f5f7a99f17e9f98cbbd96346f6c0fe7effe";
    private const string Marcador = "verificacao147" + "0123456789abcdef0123456789abcdef";

    private static RoteiroDaRetomada147 Roteiro() => RoteiroDaRetomada147.Carregar(RoteiroDaRetomada147.CaminhoComFundamento);

    [Fact]
    public void FixtureComFundamentoTemHashPreRegistradoEPreservaCenariosEEssenciaisDoAnterior()
    {
        Assert.Equal(HashDaFixtureComFundamento, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(RoteiroDaRetomada147.CaminhoComFundamento))));
        // O @1 continua intacto: é o pré-registro das retomadas de 15/11.
        Assert.Equal(CenariosDaRetomada147Tests.HashDaFixture, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(RoteiroDaRetomada147.Caminho))));
        var anterior = JsonNode.Parse(File.ReadAllBytes(RoteiroDaRetomada147.Caminho))!.AsObject();
        var novo = JsonNode.Parse(File.ReadAllBytes(RoteiroDaRetomada147.CaminhoComFundamento))!.AsObject();
        foreach (var chave in new[] { "separadorDoHistorico", "decisaoLiteral", "markdownCasoSeguinte", "consultas", "duracoes", "casos", "essenciais" })
            Assert.True(JsonNode.DeepEquals(anterior[chave], novo[chave]), "difere do @1: " + chave);
        Assert.Equal("issue147-cenarios@2", Roteiro().Versao); Assert.True(Roteiro().ComFundamento); Assert.False(RoteiroDaRetomada147.Carregar().ComFundamento);
        // Essenciais pré-registrados por cenário: o total declarado é exatamente o conjunto avaliado.
        Assert.Equal([18, 30, 48], RoteiroDaRetomada147.Duracoes.Select(Roteiro().TotalDeEssenciais));
        Assert.All(RoteiroDaRetomada147.Duracoes, d => Assert.Equal(Roteiro().TotalDeEssenciais(d), EssenciaisDaRetomada147.ChavesEsperadas(Roteiro(), d).Count));
        Assert.Equal([2, 6, 12], RoteiroDaRetomada147.Duracoes.Select(Roteiro().Turnos));
    }

    [Fact]
    public void PedidosExigemFundamentoEAsCitacoesPreRegistradasCobremCadaDocumentoSemLacuna()
    {
        var roteiro = Roteiro();
        foreach (var caso in roteiro.Casos("longo"))
        {
            var pedido = roteiro.PedidoDoCaso(caso, Marcador);
            Assert.Contains("campo fundamento", pedido); Assert.Contains("em citacao, linhas inteiras", pedido);
            Assert.Contains("Não calcule intervalos, hashes nem referências de trecho", pedido);
            Assert.DoesNotContain("na justificativa", pedido);
            Assert.DoesNotMatch(@"\{[a-z]+\}", pedido);
            Assert.Equal(caso.Ordem == 1, pedido.Contains("Decisao/DitoPeloUsuario, sem fundamento", StringComparison.Ordinal));
            var md = roteiro.Markdown(caso, Marcador);
            var (incidente, solucao) = roteiro.Citacoes(caso, Marcador);
            Assert.Equal(md, incidente + "\n" + solucao);
            // Cada citação ocorre uma única vez, começa em início de linha e contém o texto real do caso.
            foreach (var c in new[] { incidente, solucao }) Assert.Equal(md.IndexOf(c, StringComparison.Ordinal), md.LastIndexOf(c, StringComparison.Ordinal));
            Assert.StartsWith("# ", incidente); Assert.EndsWith(caso.Incidente, incidente); Assert.StartsWith("## Solução\n" + caso.Solucao, solucao);
            Assert.EndsWith("Identificador exclusivo do ensaio: " + RoteiroDaRetomada147.Identificador(caso.Ordem, Marcador), solucao);
        }
        var snapshot = roteiro.PedidoDoSnapshot("conhecimento:x@1, fonte:y@1", Guid.Empty);
        Assert.Contains("sem recontá-los em progresso ou ultimo_resultado", snapshot);
        Assert.Equal(("conhecimento:x@1, fonte:y@1", Guid.Empty.ToString()), roteiro.LerPedidoDoSnapshot(snapshot));
        Assert.Throws<InvalidOperationException>(() => RoteiroDaRetomada147.Carregar().Citacoes(roteiro.Casos("curto")[0], Marcador));
    }

    // Curto, médio e longo pelo @2: estado coerente com o roteiro, trechos íntegros, fonte omitida exatamente quando a
    // cobertura está comprovada, relações dos casos presentes, mesmo pacote numa segunda sessão. Os essenciais do pacote
    // são registrados por duração; a exigência de 100% vale para o curto, e o médio e o longo ficam como dado pré-registrado
    // (teto do bootstrap: 2048 tokens estimados e 12 itens), sem ajuste de critério.
    [PostgreSqlTheory]
    [InlineData("curto")]
    [InlineData("medio")]
    [InlineData("longo")]
    public async Task MontagemPeloRoteiroComFundamentoOmiteAFonteSoComCoberturaComprovada(string duracao)
    {
        var roteiro = Roteiro();
        var m = await MontagemDaRetomada147.MontarAsync(roteiro, duracao, Marcador);
        var casos = roteiro.Casos(duracao);
        var divergencias = new List<string>();
        VerificacaoDaRetomada147.VerificarEstado(m.Estado, roteiro, casos, Marcador, divergencias);
        Assert.True(divergencias.Count == 0, "Estado: " + string.Join("; ", divergencias));
        var consolidacao = ConsolidacaoDaRetomada147.Verificar(m.Texto, m.Estado, roteiro, casos, Marcador);
        Assert.True(consolidacao.Divergencias.Count == 0, "Consolidação: " + string.Join("; ", consolidacao.Divergencias));
        Assert.All(consolidacao.Casos, c => Assert.True(c.TrechosIntegros, $"caso {c.Ordem}: trechos"));
        Assert.Equal(m.Texto, m.TextoDaSegundaSessao); // continuidade: outra sessão, mesmo Brain, mesmo pacote
        // Relações: todo par incidente/solução selecionado no pacote traz o ResolvidoPor do próprio caso.
        var linhas = Linhas(m.Texto);
        foreach (var caso in casos)
        {
            var id = RoteiroDaRetomada147.Identificador(caso.Ordem, Marcador);
            var inc = EstadoExportado147.Ref("conhecimento", m.Estado.ConhecimentosDoCaso(Dante.Domain.Conhecimentos.TipoDeConhecimento.Incidente, id).Single());
            var sol = EstadoExportado147.Ref("conhecimento", m.Estado.ConhecimentosDoCaso(Dante.Domain.Conhecimentos.TipoDeConhecimento.Solucao, id).Single());
            if (linhas.TryGetValue(inc, out var li) && linhas.ContainsKey(sol))
                Assert.Contains("ResolvidoPor " + sol, li.GetProperty("Relacoes").EnumerateArray().Select(x => x.GetString()));
        }
        var ausentes = m.Essenciais.Where(x => !x.Value).Select(x => x.Key).ToArray();
        Assert.Equal(roteiro.TotalDeEssenciais(duracao), m.Essenciais.Count);
        output.WriteLine($"{duracao}: {Encoding.UTF8.GetByteCount(m.Texto)} B, {m.Itens} itens, estimativa do produto {m.TokensEstimados} tokens (não medida); " +
            $"essenciais {m.Essenciais.Count - ausentes.Length}/{m.Essenciais.Count}; ausentes [{string.Join(", ", ausentes)}]; " +
            $"fontes {string.Join(", ", consolidacao.Casos.Select(c => $"c{c.Ordem}={c.Situacao}"))}; sha256 {Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(m.Texto)))}");
        output.WriteLine(m.Texto);
        if (duracao == "curto")
        {
            Assert.Empty(ausentes);
            Assert.Equal(ConsolidacaoDaRetomada147.SituacaoDaFonte.Omitida, consolidacao.Casos.Single().Situacao);
        }
    }

    // A Solucao capturada sem fundamento (proveniência só da conversa) não prova cobertura: a fonte segue integral, e o
    // verificador não acusa omissão indevida nem exige a omissão.
    [PostgreSqlFact]
    public async Task SemFundamentoNaSolucaoAFonteSegueIntegral()
    {
        var roteiro = Roteiro();
        var m = await MontagemDaRetomada147.MontarAsync(roteiro, "curto", Marcador, solucaoComFundamento: false);
        var r = ConsolidacaoDaRetomada147.Verificar(m.Texto, m.Estado, roteiro, roteiro.Casos("curto"), Marcador);
        var caso = Assert.Single(r.Casos);
        Assert.Equal(ConsolidacaoDaRetomada147.SituacaoDaFonte.Integral, caso.Situacao);
        Assert.False(caso.Comprovada);
        Assert.Contains(r.Divergencias, d => d.Contains("trecho do Solucao inválido", StringComparison.Ordinal));
        Assert.DoesNotContain(r.Divergencias, d => d.Contains("omitida sem cobertura", StringComparison.Ordinal) || d.Contains("seguiu integral", StringComparison.Ordinal));
        Assert.All(m.Essenciais, e => Assert.True(e.Value, e.Key));
    }

    // Adulterações sobre um pacote e um estado válidos do curto: cada uma precisa ser recusada pelo verificador.
    [PostgreSqlFact]
    public async Task VerificadorRecusaOmissaoSemProvaIntegralComProvaFonteParcialETrechoAdulterado()
    {
        var roteiro = Roteiro(); var casos = roteiro.Casos("curto");
        var m = await MontagemDaRetomada147.MontarAsync(roteiro, "curto", Marcador);
        Assert.Empty(ConsolidacaoDaRetomada147.Verificar(m.Texto, m.Estado, roteiro, casos, Marcador).Divergencias);
        var linhas = m.Texto.Split('\n');
        var id = RoteiroDaRetomada147.Identificador(1, Marcador);
        var sol = EstadoExportado147.Ref("conhecimento", m.Estado.ConhecimentosDoCaso(Dante.Domain.Conhecimentos.TipoDeConhecimento.Solucao, id).Single());
        var fonte = m.Estado.FontesDoCaso(roteiro, casos[0], Marcador).Single();
        var refFonte = EstadoExportado147.Ref("fonte", fonte);
        string Divergencias(string texto, EstadoExportado147? estado = null) => string.Join("; ", ConsolidacaoDaRetomada147.Verificar(texto, estado ?? m.Estado, roteiro, casos, Marcador).Divergencias);
        string TrocarFonte(Func<JsonObject, JsonObject> f) => string.Join('\n', linhas.Select(l => l.StartsWith("{\"Ref\":\"" + refFonte + "\"", StringComparison.Ordinal)
            ? f(JsonNode.Parse(l)!.AsObject()).ToJsonString(new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) : l));

        // 1. Solução fora do pacote com a fonte ainda omitida: omissão sem cobertura comprovada.
        Assert.Contains("fonte omitida sem cobertura comprovada", Divergencias(string.Join('\n', linhas.Where(l => !l.StartsWith("{\"Ref\":\"" + sol + "\"", StringComparison.Ordinal)))));
        // 2. Cobertura comprovada, mas a fonte integral.
        Assert.Contains("seguiu integral", Divergencias(TrocarFonte(o => { o["Conteudo"] = EstadoExportado147.Str(fonte, "Conteudo"); return o; })));
        // 3. Fonte parcial no lugar da íntegra ou do marcador.
        Assert.Contains("não é a íntegra nem o marcador", Divergencias(TrocarFonte(o => { o["Conteudo"] = roteiro.Citacoes(casos[0], Marcador).Incidente; return o; })));
        // 4. ConsolidadaEm sem um dos conhecimentos que cobrem a fonte.
        Assert.Contains("ConsolidadaEm difere", Divergencias(TrocarFonte(o => { o["ConsolidadaEm"] = new JsonArray(JsonValue.Create(sol)); return o; })));
        // 5. Trecho adulterado no estado (sha256 do intervalo): a cobertura deixa de estar comprovada.
        var json = m.EstadoJson; var refAtual = Regex147.ShaDoTrecho().Matches(json).Select(x => x.Groups[1].Value).First();
        var adulterado = new EstadoExportado147(json.Replace(refAtual, new string('0', 64), StringComparison.Ordinal));
        var d5 = Divergencias(m.Texto, adulterado);
        Assert.Contains("sha256 do trecho diverge", d5); Assert.Contains("fonte omitida sem cobertura comprovada", d5);
    }

    private static Dictionary<string, JsonElement> Linhas(string texto) => texto.Split('\n').Where(l => l.StartsWith('{'))
        .Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToDictionary(l => l.GetProperty("Ref").GetString()!);
}

internal static partial class Regex147
{
    [System.Text.RegularExpressions.GeneratedRegex(@"(?:&|\\u0026)sha256=([0-9a-f]{64})")]
    internal static partial System.Text.RegularExpressions.Regex ShaDoTrecho();
}

// Estado e pacote de uma retomada montada pelo @2 sem agente real: o "agente" é o próprio roteiro cumprido à letra.
internal sealed record MontagemDaRetomada147(string Texto, string TextoDaSegundaSessao, string EstadoJson, EstadoExportado147 Estado,
    IReadOnlyDictionary<string, bool> Essenciais, int Itens, int TokensEstimados)
{
    internal static async Task<MontagemDaRetomada147> MontarAsync(RoteiroDaRetomada147 roteiro, string duracao, string marcador, bool solucaoComFundamento = true)
    {
        await using var c = await ProvenienciaPorTrecho147Tests.Cenario.CriarAsync();
        var casos = roteiro.Casos(duracao);
        Guid? decisao = null; var referencias = new List<string>();
        static JsonElement Json(object? v) => JsonSerializer.SerializeToElement(v);
        for (var i = 0; i < casos.Count; i++)
        {
            var caso = casos[i]; var desloc = 100 * i;
            var md = roteiro.Markdown(caso, marcador);
            var fonte = await c.EvidenciaAsync(md, $"Caso real do D.A.N.T.E.: PR #{caso.Pr} (teste-147)", 123 + desloc);
            var pedido = BrainMcpTests.Mensagem(roteiro.PedidoDoCaso(caso, marcador), 124 + desloc);
            var (citacaoIncidente, citacaoSolucao) = roteiro.Citacoes(caso, marcador);
            var linhaDoIdentificador = "Identificador exclusivo do ensaio: " + RoteiroDaRetomada147.Identificador(caso.Ordem, marcador);
            async Task<(Guid Id, int Revisao)> CapturarAsync(string tipo, string conteudo, string? citacao)
            {
                object args = citacao is not null
                    ? new { titulo = $"{tipo} do PR #{caso.Pr}", conteudo, tipo, natureza = "ConclusaoDoAgente", tags = new[] { "teste-147" }, sensibilidade = "Pessoal",
                        justificativa = "Derivado da evidência do caso.", fundamento = new { fonte = fonte.Ref, citacao } }
                    : new { titulo = $"{tipo} do PR #{caso.Pr}", conteudo, tipo, natureza = "ConclusaoDoAgente", tags = new[] { "teste-147" }, sensibilidade = "Pessoal",
                        justificativa = "Derivado da evidência " + fonte.Ref + "." };
                var candidato = Json(await c.OperarAsync(pedido, "brain_capturar_conhecimento", args)).GetProperty("candidato");
                return (candidato.GetProperty("Id").GetGuid(), candidato.GetProperty("Revisao").GetInt32());
            }
            var inc = await CapturarAsync("Incidente", citacaoIncidente + "\n" + linhaDoIdentificador, citacaoIncidente);
            var sol = await CapturarAsync("Solucao", citacaoSolucao, solucaoComFundamento ? citacaoSolucao : null);
            await c.OperarAsync(pedido, "brain_vincular_candidatos", new { incidente = inc.Id, revisao_incidente = inc.Revisao, solucao = sol.Id, revisao_solucao = sol.Revisao });
            var idInc = await c.ConfirmarAsync(inc.Id, inc.Revisao, 125 + desloc); var idSol = await c.ConfirmarAsync(sol.Id, sol.Revisao, 126 + desloc);
            if (i == 0)
            {
                var dec = Json(await c.OperarAsync(BrainMcpTests.Mensagem(roteiro.DecisaoLiteral, 127), "brain_capturar_conhecimento", new { titulo = "Decisão: o Brain deve permanecer em modo lexical",
                    conteudo = roteiro.DecisaoLiteral, tipo = "Decisao", natureza = "DitoPeloUsuario", tags = new[] { "teste-147" }, sensibilidade = "Pessoal", justificativa = "Decisão literal do usuário." })).GetProperty("candidato");
                decisao = await c.ConfirmarAsync(dec.GetProperty("Id").GetGuid(), dec.GetProperty("Revisao").GetInt32(), 128);
            }
            int Revisao(Guid id) { using var db = c.Banco.Contexto(); return db.Conhecimentos.Single(x => x.Id == id).Revisao; }
            if (i == 0) referencias.Add($"conhecimento:{decisao:D}@{Revisao(decisao!.Value)}");
            referencias.AddRange([$"conhecimento:{idInc:D}@{Revisao(idInc)}", $"conhecimento:{idSol:D}@{Revisao(idSol)}", fonte.Ref]);
            // Snapshot do @2: objetivo, tarefa, pendências e próximo passo; registros por referência, sem progresso recontado.
            int revisaoDoSnapshot; await using (var db = c.Banco.Contexto()) revisaoDoSnapshot = await db.Set<ContextoDeTrabalho>().Select(x => x.Revisao).SingleOrDefaultAsync();
            await c.OperarAsync(BrainMcpTests.Mensagem("atualize o snapshot", 129 + desloc), "brain_atualizar_contexto_de_trabalho", new
            {
                revisao = revisaoDoSnapshot,
                objetivo = $"Validar a continuidade independente do Brain na Issue #147 com {i + 1} caso(s) real(is) do D.A.N.T.E. Ensaio: {marcador}",
                tarefa = $"Ensaio teste-147 ({marcador}): manter o estado operacional dos casos registrados, referenciando evidências, Incidentes, Solucoes e a Decisao no Brain.",
                ids_decisoes_confirmadas = new[] { decisao!.Value }, referencias,
                pendencias = new[] { "Preservar Worker ativo e banco canônico", "Busca do Brain permanece lexical" },
                proximos_passos = new[] { "Validar a continuidade independente do Brain na Issue #147 em nova sessão, sem iniciar a Issue #282" }
            });
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var b = await RestauracaoDoBrain147.ReproduzirAsync(c.Banco.ConnectionString, roteiro.ConsultaB(duracao), timeout.Token);
        var b2 = await RestauracaoDoBrain147.ReproduzirAsync(c.Banco.ConnectionString, roteiro.ConsultaB(duracao), timeout.Token);
        string estado; await using (var db = c.Banco.Contexto()) estado = await RestauracaoDoBrain147.EstadoPersistidoAsync(db, timeout.Token);
        var texto = Encoding.UTF8.GetString(b.TextoDoPacote); var exportado = new EstadoExportado147(estado);
        return new(texto, Encoding.UTF8.GetString(b2.TextoDoPacote), estado, exportado,
            EssenciaisDaRetomada147.AvaliarPacote(texto, exportado, roteiro, duracao, marcador), b.Pacote.Itens.Count, b.Pacote.Custo.TokensEstimados);
    }
}
