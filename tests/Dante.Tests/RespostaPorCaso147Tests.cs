using System.Security.Cryptography;
using System.Text.Json;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;
using Dante.Domain.RelacoesDeConhecimento;

namespace Dante.Tests;

// Gates da retomada da #147 por caso: essenciais do pacote e critérios da resposta B (identificação, incidente, solução,
// origem e relação de cada caso). Sem CLI: estado, pacote, resposta e rastro sintéticos coerentes com o roteiro médio.
public sealed class RespostaPorCaso147Tests
{
    // Publicado na #147 antes de qualquer execução; cenarios.json mantém o próprio hash (CenariosDaRetomada147Tests).
    internal const string HashDosCriterios = "f8bf27a2d9bef5c73d5aa5369152693dcfd26c0a38abd47277cae0ea5987b2e2";
    private const string Marcador = "verificacao147" + "0123456789abcdef0123456789abcdef";
    private const string Sessao = "instancia-teste:147A";

    [Fact]
    public void CriteriosTemOHashPreRegistradoEDeixamACenariosInalterado()
    {
        Assert.Equal(HashDosCriterios, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(CriteriosDaRespostaB147.Caminho))));
        Assert.Equal(CenariosDaRetomada147Tests.HashDaFixture, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(RoteiroDaRetomada147.Caminho))));
    }

    // Cada critério é atendido pelo texto literal do próprio caso (o que está no Brain) e por nenhum outro caso:
    // trocar a solução ou o incidente entre casos sempre reprova.
    [Fact]
    public void CriteriosSaoDerivaveisDoProprioCasoEExclusivosEntreCasos()
    {
        var roteiro = RoteiroDaRetomada147.Carregar(); var criterios = CriteriosDaRespostaB147.Carregar();
        var casos = roteiro.Casos("longo").Where(c => c.Ordem > 1).ToArray();
        Assert.Equal(casos.Select(c => c.Ordem).Order(), criterios.Casos.Order());
        foreach (var caso in casos)
        {
            var (incidente, solucao) = criterios.DoCaso(caso.Ordem);
            Assert.All(incidente.Concat(solucao).SelectMany(g => g), t => Assert.Equal(AvaliacaoDoHarness147.Normalizar(t), t));
            Assert.True(CriteriosDaRespostaB147.Atende(caso.Incidente, incidente), $"caso {caso.Ordem}: incidente literal não atende");
            Assert.True(CriteriosDaRespostaB147.Atende(caso.Solucao, solucao), $"caso {caso.Ordem}: solução literal não atende");
            foreach (var outro in roteiro.Casos("longo").Where(o => o.Ordem != caso.Ordem))
            {
                Assert.False(CriteriosDaRespostaB147.Atende(outro.Solucao, solucao), $"solução do caso {outro.Ordem} atende o caso {caso.Ordem}");
                Assert.False(CriteriosDaRespostaB147.Atende(outro.Incidente, incidente), $"incidente do caso {outro.Ordem} atende o caso {caso.Ordem}");
            }
        }
        Assert.Throws<KeyNotFoundException>(() => criterios.DoCaso(1));
        Assert.False(CriteriosDaRespostaB147.Atende("qualquer texto", []));
    }

    [Fact]
    public void CurtoMantemSoOsCriteriosHistoricos()
    {
        var roteiro = RoteiroDaRetomada147.Carregar();
        Assert.Empty(RespostaPorCaso147.ChavesEsperadas(roteiro, "curto"));
        Assert.Empty(RespostaPorCaso147.Avaliar("qualquer resposta", roteiro, CriteriosDaRespostaB147.Carregar(), "curto", Marcador, _ => (false, false)));
        Assert.Equal(18, EssenciaisDaRetomada147.ChavesEsperadas(roteiro, "curto").Count);
        Assert.Equal(13, RespostaPorCaso147.ChavesEsperadas(roteiro, "medio").Count);
        Assert.Equal(28, RespostaPorCaso147.ChavesEsperadas(roteiro, "longo").Count);
        Assert.Equal(18 + 6 * 5, EssenciaisDaRetomada147.ChavesEsperadas(roteiro, "longo").Count);
    }

    [Fact]
    public void CenarioMedioCompletoPassaEmTodosOsGates()
    {
        var c = Sintetico.Medio();
        var divergencias = new List<string>();
        VerificacaoDaRetomada147.VerificarEstado(c.Estado, c.Roteiro, c.Casos, Marcador, divergencias);
        Assert.Empty(divergencias);
        var pacote = EssenciaisDaRetomada147.AvaliarPacote(c.Pacote, c.Estado, c.Roteiro, "medio", Marcador);
        RespostaPorCaso147.Exigir(pacote, EssenciaisDaRetomada147.ChavesEsperadas(c.Roteiro, "medio"), "Essenciais do pacote");
        var resposta = c.AvaliarResposta(c.Resposta, c.Rastro);
        RespostaPorCaso147.Exigir(resposta, RespostaPorCaso147.ChavesEsperadas(c.Roteiro, "medio"), "Critérios por caso");
        Assert.All(AvaliacaoDoHarness147.AvaliarResposta(c.Resposta, Marcador).Where(x => x.Key is "incidente" or "solucao" or "marcador_exclusivo"), x => Assert.True(x.Value, x.Key));
    }

    [Fact]
    public void PacoteSemEssencialDeCasoPosteriorReprova()
    {
        var c = Sintetico.Medio();
        // Truncamento do caso 3: fonte e incidente fora do pacote; a solução do caso continua.
        var truncado = string.Join('\n', c.Pacote.Split('\n').Where(l => !l.Contains(c.Ref(3, "fonte")) && !l.Contains(c.Ref(3, "incidente"))));
        var r = EssenciaisDaRetomada147.AvaliarPacote(truncado, c.Estado, c.Roteiro, "medio", Marcador);
        Assert.False(r["c3.incidente"]); Assert.False(r["c3.ref_fonte"]); Assert.False(r["c3.commit"]);
        Assert.True(r["c3.solucao"]);
        Assert.All(r.Where(x => !x.Key.StartsWith("c3.", StringComparison.Ordinal)), x => Assert.True(x.Value, x.Key));
        var erro = Assert.ThrowsAny<Exception>(() => RespostaPorCaso147.Exigir(r, EssenciaisDaRetomada147.ChavesEsperadas(c.Roteiro, "medio"), "Essenciais do pacote"));
        Assert.Contains("c3.incidente", erro.Message);
        // Um único essencial a menos (o commit do caso 2, só na fonte) também reprova.
        var semCommit = c.Pacote.Replace(c.Casos[1].Commit, "0000000", StringComparison.Ordinal);
        var r2 = EssenciaisDaRetomada147.AvaliarPacote(semCommit, c.Estado, c.Roteiro, "medio", Marcador);
        Assert.Equal(["c2.commit"], r2.Where(x => !x.Value).Select(x => x.Key));
        Assert.ThrowsAny<Exception>(() => RespostaPorCaso147.Exigir(r2, EssenciaisDaRetomada147.ChavesEsperadas(c.Roteiro, "medio"), "Essenciais do pacote"));
    }

    [Fact]
    public void RespostaSemSolucaoDeCasoPosteriorReprova()
    {
        var c = Sintetico.Medio();
        var resposta = c.Resposta.Replace(c.LinhaDaSolucao(3) + "\n", "", StringComparison.Ordinal);
        Assert.NotEqual(c.Resposta, resposta);
        var r = c.AvaliarResposta(resposta, c.Rastro);
        Assert.Equal(["c3.solucao"], r.Where(x => !x.Value).Select(x => x.Key));
        Assert.ThrowsAny<Exception>(() => RespostaPorCaso147.Exigir(r, RespostaPorCaso147.ChavesEsperadas(c.Roteiro, "medio"), "Critérios por caso"));
    }

    [Fact]
    public void RespostaComSolucaoIncorretaOuTrocadaReprova()
    {
        var c = Sintetico.Medio();
        // Caso 2 com a solução do caso 3.
        var incorreta = c.Resposta.Replace(c.LinhaDaSolucao(2), "Solução: " + c.Casos[2].Solucao, StringComparison.Ordinal);
        Assert.Equal(["c2.solucao"], c.AvaliarResposta(incorreta, c.Rastro).Where(x => !x.Value).Select(x => x.Key));
        // Soluções dos casos 2 e 3 trocadas entre as seções: os termos existem na resposta, mas no caso errado.
        var trocada = c.Resposta.Replace(c.LinhaDaSolucao(2), "\u0001", StringComparison.Ordinal)
            .Replace(c.LinhaDaSolucao(3), c.LinhaDaSolucao(2), StringComparison.Ordinal).Replace("\u0001", c.LinhaDaSolucao(3), StringComparison.Ordinal);
        Assert.Equal(["c2.solucao", "c3.solucao"], c.AvaliarResposta(trocada, c.Rastro).Where(x => !x.Value).Select(x => x.Key).Order());
        // Solução do caso 1 escrita sob o caso 2: o predicado histórico, aplicado ao texto do caso 1, reprova.
        var caso1Deslocado = c.Resposta.Replace(c.LinhaDaSolucao(1) + "\n", "", StringComparison.Ordinal)
            .Replace(c.LinhaDaSolucao(2), c.LinhaDaSolucao(2) + "\n" + c.LinhaDaSolucao(1), StringComparison.Ordinal);
        Assert.Equal(["c1.solucao"], c.AvaliarResposta(caso1Deslocado, c.Rastro).Where(x => !x.Value).Select(x => x.Key));
        // Só identificador e número da PR, sem incidente nem solução: insuficiente.
        var soIdentificacao = string.Join("\n\n", c.Casos.Select(k => $"PR #{k.Pr} — {RoteiroDaRetomada147.Identificador(k.Ordem, Marcador)}"));
        var r = c.AvaliarResposta(soIdentificacao, c.Rastro);
        Assert.All(c.Casos, k => { Assert.True(r[$"c{k.Ordem}.identificacao"]); Assert.False(r[$"c{k.Ordem}.incidente"]); Assert.False(r[$"c{k.Ordem}.solucao"]); });
    }

    [Fact]
    public void IdentificacaoExigeIdentificadorEPrDoMesmoCaso()
    {
        var c = Sintetico.Medio();
        // Identificador do caso 3 ao lado da PR do caso 2: nenhum dos dois casos fica identificado por esse parágrafo.
        var cruzada = c.Resposta.Replace($"PR #{c.Casos[2].Pr} ({RoteiroDaRetomada147.Identificador(3, Marcador)})",
            $"PR #{c.Casos[1].Pr} ({RoteiroDaRetomada147.Identificador(3, Marcador)})", StringComparison.Ordinal);
        Assert.NotEqual(c.Resposta, cruzada);
        Assert.False(c.AvaliarResposta(cruzada, c.Rastro)["c3.identificacao"]);
    }

    [Fact]
    public void RastroSemOrigemOuRelacaoDeCasoPosteriorReprova()
    {
        var c = Sintetico.Medio();
        var semOrigem = c.RastroSem("brain_mostrar_origem", c.Id(3, "incidente"));
        Assert.Equal(["c3.origem_consultada"], c.AvaliarResposta(c.Resposta, semOrigem).Where(x => !x.Value).Select(x => x.Key));
        var semRelacao = c.RastroSem("brain_listar_relacoes", c.Id(2, "incidente"));
        Assert.Equal(["c2.relacao_consultada"], c.AvaliarResposta(c.Resposta, semRelacao).Where(x => !x.Value).Select(x => x.Key));
        // Consulta com erro de aplicação não comprova.
        var comErro = new RastroDeFerramentas147();
        comErro.Chamadas.AddRange(c.Rastro.Chamadas.Select(x => x.Nome == "brain_mostrar_origem" && x.Saida!.Contains(c.Id(3, "incidente"))
            ? x with { Saida = JsonSerializer.Serialize(new { erro = "nao_encontrado", id = c.Id(3, "incidente"), sessao = Sessao }) } : x));
        Assert.False(c.AvaliarResposta(c.Resposta, comErro)["c3.origem_consultada"]);
    }

    [Fact]
    public void ExigirRecusaUnidadeFaltandoOuAMais()
    {
        var esperadas = new[] { "c1.a", "c2.b" };
        Assert.ThrowsAny<Exception>(() => RespostaPorCaso147.Exigir(new Dictionary<string, bool>(), esperadas, "x"));
        Assert.ThrowsAny<Exception>(() => RespostaPorCaso147.Exigir(new Dictionary<string, bool> { ["c1.a"] = true }, esperadas, "x"));
        Assert.ThrowsAny<Exception>(() => RespostaPorCaso147.Exigir(new Dictionary<string, bool> { ["c1.a"] = true, ["c2.b"] = true, ["c3.c"] = true }, esperadas, "x"));
        RespostaPorCaso147.Exigir(new Dictionary<string, bool> { ["c1.a"] = true, ["c2.b"] = true }, esperadas, "x");
    }

    // Execução médio coerente com o roteiro, montada sem CLI: um caso = fonte literal, incidente e solução inferidos com o
    // identificador do caso e ResolvidoPor; uma decisão confirmada; pacote, resposta B e rastro com todos os casos.
    private sealed class Sintetico
    {
        internal RoteiroDaRetomada147 Roteiro { get; } = RoteiroDaRetomada147.Carregar();
        internal IReadOnlyList<CasoDaRetomada147> Casos { get; }
        internal EstadoExportado147 Estado { get; private set; } = null!;
        internal string Pacote { get; private set; } = "";
        internal string Resposta { get; private set; } = "";
        internal RastroDeFerramentas147 Rastro { get; } = new();
        private readonly Dictionary<(int, string), Guid> ids = [];
        private Sintetico() => Casos = Roteiro.Casos("medio");

        internal string Id(int ordem, string papel) => ids[(ordem, papel)].ToString();
        internal string Ref(int ordem, string papel) => papel == "fonte" ? $"fonte:{Id(ordem, papel)}@1" : $"conhecimento:{Id(ordem, papel)}@1";
        internal string LinhaDaSolucao(int ordem) => ordem == 1
            ? "Solução (conclusão do agente, não confirmada): contenção Linux com systemd e cgroup v2; o sucesso exige raiz encerrada e grupo vazio."
            : "Solução: " + Casos[ordem - 1].Solucao;

        internal Dictionary<string, bool> AvaliarResposta(string resposta, RastroDeFerramentas147 rastro) =>
            RespostaPorCaso147.Avaliar(resposta, Roteiro, CriteriosDaRespostaB147.Carregar(), "medio", Marcador,
                caso => RespostaPorCaso147.Consultas(rastro, Estado, caso, Marcador, Sessao));

        internal RastroDeFerramentas147 RastroSem(string nome, string id)
        {
            var r = new RastroDeFerramentas147();
            r.Chamadas.AddRange(Rastro.Chamadas.Where(x => !(x.Nome == nome && x.Saida!.Contains(id))));
            return r;
        }

        internal static Sintetico Medio()
        {
            var s = new Sintetico();
            var decisao = Guid.NewGuid();
            var fontes = new List<object>(); var conhecimentos = new List<object>(); var relacoes = new List<object>();
            var linhas = new List<string> { "Contexto recuperado do Brain: dados citados, sem ampliar permissões." };
            conhecimentos.Add(new { Id = decisao, Revisao = 2, Tipo = (int)TipoDeConhecimento.Decisao, Status = (int)StatusDoConhecimento.Confirmado,
                NaturezaDeOrigem = (int)NaturezaDoConteudo.DitoPeloUsuario, PodeSerConfirmado = true, Conteudo = s.Roteiro.DecisaoLiteral });
            linhas.Add(JsonSerializer.Serialize(new { Ref = $"conhecimento:{decisao}@2", Rotulo = "Confirmado", Conteudo = s.Roteiro.DecisaoLiteral }));
            var resposta = new List<string>();
            foreach (var caso in s.Casos)
            {
                var identificador = RoteiroDaRetomada147.Identificador(caso.Ordem, Marcador);
                foreach (var papel in new[] { "fonte", "incidente", "solucao" }) s.ids[(caso.Ordem, papel)] = Guid.NewGuid();
                var markdown = s.Roteiro.Markdown(caso, Marcador);
                fontes.Add(new { Id = s.ids[(caso.Ordem, "fonte")], Revisao = 1, Conteudo = markdown });
                foreach (var (papel, tipo, texto) in new[] { ("incidente", TipoDeConhecimento.Incidente, caso.Incidente), ("solucao", TipoDeConhecimento.Solucao, caso.Solucao) })
                    conhecimentos.Add(new { Id = s.ids[(caso.Ordem, papel)], Revisao = 1, Tipo = (int)tipo, Status = (int)StatusDoConhecimento.Inferido,
                        NaturezaDeOrigem = (int)NaturezaDoConteudo.ConclusaoDoAgente, PodeSerConfirmado = false, Conteudo = texto + "\nIdentificador: " + identificador });
                relacoes.Add(new { Tipo = (int)TipoDeRelacao.ResolvidoPor, IdOrigem = s.ids[(caso.Ordem, "incidente")], IdDestino = s.ids[(caso.Ordem, "solucao")] });
                linhas.Add(JsonSerializer.Serialize(new { Ref = s.Ref(caso.Ordem, "fonte"), Rotulo = "Fonte bruta de autoria do agente, não confirmada", Conteudo = markdown }));
                linhas.Add(JsonSerializer.Serialize(new { Ref = s.Ref(caso.Ordem, "incidente"), Rotulo = "Conclusão do agente, não confirmada",
                    Conteudo = caso.Incidente + "\nIdentificador: " + identificador, Relacoes = new[] { "ResolvidoPor " + s.Ref(caso.Ordem, "solucao") } }));
                linhas.Add(JsonSerializer.Serialize(new { Ref = s.Ref(caso.Ordem, "solucao"), Rotulo = "Conclusão do agente, não confirmada", Conteudo = caso.Solucao + "\nIdentificador: " + identificador }));
                resposta.Add(caso.Ordem == 1
                    ? $"## Caso 1 — PR #{caso.Pr} ({identificador})\nIncidente (conclusão do agente, não confirmada): Completion podia sinalizar término antes de confirmar a limpeza dos processos descendentes.\n{s.LinhaDaSolucao(1)}\nDecisão confirmada: o Brain deve permanecer em modo lexical.\nFonte bruta: o Markdown do caso, não confirmado.\n"
                    : $"## Caso {caso.Ordem} — PR #{caso.Pr} ({identificador})\nIncidente: {caso.Incidente}\n{s.LinhaDaSolucao(caso.Ordem)}\n");
                s.Rastro.Chamadas.Add(new("o" + caso.Ordem, "dante_brain", "brain_mostrar_origem", "",
                    true, JsonSerializer.Serialize(new { id = s.ids[(caso.Ordem, "incidente")], origem = "conhecimento", sessao = Sessao })));
                s.Rastro.Chamadas.Add(new("r" + caso.Ordem, "dante_brain", "brain_listar_relacoes", "",
                    true, JsonSerializer.Serialize(new { relacoes = new[] { new { origem = s.ids[(caso.Ordem, "incidente")], destino = s.ids[(caso.Ordem, "solucao")], tipo = "ResolvidoPor" } } })));
            }
            var snapshot = new { Dados = new { IdsDecisoesConfirmadas = new[] { decisao }, Referencias = s.Casos.Select(k => s.Ref(k.Ordem, "fonte")).ToArray() } };
            s.Estado = new EstadoExportado147(JsonSerializer.Serialize(new { fontes, conhecimentos, candidatos = Array.Empty<object>(), vinculos = Array.Empty<object>(), relacoes, snapshots = new[] { snapshot } }));
            s.Pacote = string.Join('\n', linhas) + "\n";
            s.Resposta = string.Join("\n", resposta);
            return s;
        }
    }
}
