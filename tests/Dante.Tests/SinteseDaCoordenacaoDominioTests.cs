using System.Text.Json;
using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;
using static Dante.Tests.CoordenacaoDominioTests;

namespace Dante.Tests;

// #310 (AD-75, D7), PR 3: a síntese do coordenador é validada só na estrutura, contra a trilha mecânica: cobertura de
// 100% das unidades de todas as revisões, citações apontando para resultados persistidos pelo hash, divergência com ao
// menos duas unidades, unidade sem resposta só em inconclusivos, nenhum campo desconhecido. Forma canônica e hash
// calculados no domínio; a entidade só nasce da etapa de síntese em curso, com a sessão vinculada.
public sealed class SinteseDaCoordenacaoDominioTests
{
    private static readonly string HashIndice = new string('a', 60) + "0001";
    private static readonly string HashConsulta = new string('b', 60) + "0002";

    // Revisão 1: indice respondida, consulta sem resposta; revisão 2: consulta respondida.
    private static readonly IReadOnlyList<UnidadeDaTrilha> Trilha =
    [
        new(1, "indice", HashIndice),
        new(1, "consulta", null),
        new(2, "consulta", HashConsulta)
    ];

    private static object Citacao(int revisao, string unidade, string? hash = null) =>
        hash is null ? new { revisao, unidade } : new { revisao, unidade, hash };

    private static string Valida(string? resumo = "Resumo.") => JsonSerializer.Serialize(new
    {
        resumo,
        conclusoes = new[] { new { texto = "Ha ix_estado.", citacoes = new[] { Citacao(1, "indice", HashIndice[..12]) } } },
        divergencias = new[]
        {
            new { texto = "Cobertura.", citacoes = new[] { Citacao(2, "consulta", HashConsulta), Citacao(1, "indice", HashIndice[..20]) } }
        },
        inconclusivos = new[] { new { texto = "Sem resposta na revisao 1.", citacoes = new[] { Citacao(1, "consulta") } } }
    });

    [Fact]
    public void SinteseValidaTemFormaCanonicaComHashCompletoECitacoesOrdenadas()
    {
        var resultado = ValidacaoDaSintese.Validar(Valida(), Trilha);
        Assert.True(resultado.Valido, string.Join("; ", resultado.Recusas.Select(r => r.Mensagem)));
        var sintese = resultado.Sintese!;
        Assert.Equal(ValidacaoDoPlano.Hash(sintese.Conteudo), sintese.Hash);
        using var documento = JsonDocument.Parse(sintese.Conteudo);
        var raiz = documento.RootElement;
        Assert.Equal(["conclusoes", "divergencias", "formato", "inconclusivos", "resumo"],
            raiz.EnumerateObject().Select(p => p.Name));
        // O hash citado (prefixo) vira o hash completo da trilha; as citações ficam em ordem (revisão, unidade).
        var divergencia = raiz.GetProperty("divergencias")[0].GetProperty("citacoes");
        Assert.Equal([(1, "indice", HashIndice), (2, "consulta", HashConsulta)], divergencia.EnumerateArray()
            .Select(c => (c.GetProperty("revisao").GetInt32(), c.GetProperty("unidade").GetString(), c.GetProperty("hash").GetString())));
        Assert.Equal(JsonValueKind.Null, raiz.GetProperty("inconclusivos")[0].GetProperty("citacoes")[0].GetProperty("hash").ValueKind);
        // Relê exatamente o que gravaria; um byte a mais é recusado.
        Assert.Equal(sintese, ValidacaoDaSintese.LerCanonico(sintese.Conteudo, Trilha));
        Assert.Throws<InvalidOperationException>(() => ValidacaoDaSintese.LerCanonico(sintese.Conteudo + " ", Trilha));
        // Fora da ordem canônica das chaves, ou contra outra trilha (hash que não confere): recusada.
        Assert.Throws<InvalidOperationException>(() => ValidacaoDaSintese.LerCanonico(
            "{\"resumo\":\"Resumo.\"," + sintese.Conteudo[1..].Replace(",\"resumo\":\"Resumo.\"", ""), Trilha));
        Assert.Throws<InvalidOperationException>(() => ValidacaoDaSintese.LerCanonico(sintese.Conteudo,
            [new(1, "indice", new string('9', 64)), new(1, "consulta", null), new(2, "consulta", HashConsulta)]));
    }

    [Fact]
    public void FormaCanonicaNaoDependeDaOrdemDasChavesNemDaNormalizacaoDoTexto()
    {
        var a = ValidacaoDaSintese.Validar(Valida("Ação\r\n"), Trilha).Sintese!;
        var b = ValidacaoDaSintese.Validar(Valida("Ação").Replace("Ha ix_estado.", "Ha ix_estado.  "), Trilha).Sintese!;
        Assert.Equal(a.Hash, b.Hash);
        using var documento = JsonDocument.Parse(Valida("Ação"));
        var invertida = "{" + string.Join(",", documento.RootElement.EnumerateObject().Reverse()
            .Select(p => JsonSerializer.Serialize(p.Name) + ":" + p.Value.GetRawText())) + "}";
        Assert.Equal(a.Hash, ValidacaoDaSintese.Validar(invertida, Trilha).Sintese!.Hash);
    }

    public static TheoryData<string, MotivoDaRecusaDaSintese, string?> Recusas() => new()
    {
        { "", MotivoDaRecusaDaSintese.FormaInvalida, null },
        { "não é json", MotivoDaRecusaDaSintese.FormaInvalida, null },
        { "[]", MotivoDaRecusaDaSintese.FormaInvalida, null },
        // O hash é do servidor: enviá-lo é campo desconhecido.
        { Valida().Replace("{\"resumo\"", "{\"hash\":\"abc\",\"resumo\""), MotivoDaRecusaDaSintese.FormaInvalida, null },
        { Valida(resumo: null), MotivoDaRecusaDaSintese.TextoInvalido, "resumo" },
        { Valida(new string('r', ValidacaoDaSintese.TamanhoMaximoDoTexto + 1)), MotivoDaRecusaDaSintese.TextoInvalido, "resumo" },
        // Cobertura: a unidade sem resposta da revisão 1 não foi citada.
        {
            JsonSerializer.Serialize(new
            {
                resumo = "R.",
                conclusoes = new[] { new { texto = "C.", citacoes = new[] { Citacao(1, "indice", HashIndice), Citacao(2, "consulta", HashConsulta) } } }
            }),
            MotivoDaRecusaDaSintese.CoberturaIncompleta, "r1/consulta"
        },
        // Conclusão citando unidade sem resultado persistido.
        {
            JsonSerializer.Serialize(new
            {
                resumo = "R.",
                conclusoes = new[] { new { texto = "C.", citacoes = new[] { Citacao(1, "consulta"), Citacao(1, "indice", HashIndice), Citacao(2, "consulta", HashConsulta) } } }
            }),
            MotivoDaRecusaDaSintese.SemResultado, "conclusoes#1:r1/consulta"
        },
        // Hash que não é o do resultado persistido, curto demais ou ausente numa unidade respondida.
        { Valida().Replace(HashIndice[..12], new string('0', 12)), MotivoDaRecusaDaSintese.HashDivergente, "conclusoes#1:r1/indice" },
        { Valida().Replace(HashIndice[..12], HashIndice[..11]), MotivoDaRecusaDaSintese.HashDivergente, "conclusoes#1:r1/indice" },
        { Valida().Replace($",\"hash\":\"{HashIndice[..12]}\"", ""), MotivoDaRecusaDaSintese.HashDivergente, "conclusoes#1:r1/indice" },
        // Inconclusivo de unidade sem resposta não leva hash.
        {
            Valida().Replace("{\"revisao\":1,\"unidade\":\"consulta\"}", $"{{\"revisao\":1,\"unidade\":\"consulta\",\"hash\":\"{HashIndice}\"}}"),
            MotivoDaRecusaDaSintese.HashDivergente, "inconclusivos#1:r1/consulta"
        },
        // Divergência com uma unidade só não é divergência.
        {
            Valida().Replace($"{{\"revisao\":2,\"unidade\":\"consulta\",\"hash\":\"{HashConsulta}\"}},", ""),
            MotivoDaRecusaDaSintese.DivergenciaSemPar, "divergencias#1"
        },
        // Unidade fora da trilha, campo desconhecido na citação e no item, lista vazia de citações.
        { Valida().Replace("\"unidade\":\"indice\",\"hash\"", "\"unidade\":\"outra\",\"hash\""), MotivoDaRecusaDaSintese.UnidadeInexistente, "conclusoes#1:r1/outra" },
        { Valida().Replace("{\"revisao\":1,\"unidade\":\"consulta\"}", "{\"revisao\":1,\"unidade\":\"consulta\",\"papel\":\"rev\"}"), MotivoDaRecusaDaSintese.CitacaoInvalida, "inconclusivos#1" },
        { Valida().Replace("{\"texto\":\"Sem resposta", "{\"peso\":1,\"texto\":\"Sem resposta"), MotivoDaRecusaDaSintese.FormaInvalida, "inconclusivos#1" },
        { Valida().Replace("\"Sem resposta na revisao 1.\",\"citacoes\":[{\"revisao\":1,\"unidade\":\"consulta\"}]", "\"Sem resposta na revisao 1.\",\"citacoes\":[]"), MotivoDaRecusaDaSintese.CitacaoInvalida, "inconclusivos#1" },
        { Valida().Replace("\"Ha ix_estado.\"", "\"  \""), MotivoDaRecusaDaSintese.TextoInvalido, "conclusoes#1" }
    };

    [Theory]
    [MemberData(nameof(Recusas))]
    public void SinteseInvalidaRecusadaComMotivoEReferencia(string proposta, MotivoDaRecusaDaSintese motivo, string? referencia)
    {
        var resultado = ValidacaoDaSintese.Validar(proposta, Trilha);
        Assert.False(resultado.Valido);
        Assert.Null(resultado.Sintese);
        Assert.Contains(resultado.Recusas, r => r.Motivo == motivo && r.Referencia == referencia);
    }

    [Fact]
    public void ItensDemaisNumaListaSaoRecusados()
    {
        var itens = Enumerable.Range(0, ValidacaoDaSintese.MaximoDeItensPorLista + 1)
            .Select(_ => new { texto = "C.", citacoes = new[] { Citacao(1, "indice", HashIndice) } }).ToArray();
        var proposta = JsonSerializer.Serialize(new { resumo = "R.", conclusoes = itens });
        Assert.Contains(ValidacaoDaSintese.Validar(proposta, Trilha).Recusas,
            r => r.Motivo == MotivoDaRecusaDaSintese.ItensDemais && r.Referencia == "conclusoes");
    }

    [Fact]
    public void CitacaoRepetidaNoItemContaUmaVezSo()
    {
        var proposta = Valida().Replace($"{{\"revisao\":1,\"unidade\":\"indice\",\"hash\":\"{HashIndice[..12]}\"}}",
            $"{{\"revisao\":1,\"unidade\":\"indice\",\"hash\":\"{HashIndice[..12]}\"}},{{\"revisao\":1,\"unidade\":\"indice\",\"hash\":\"{HashIndice}\"}}");
        var sintese = ValidacaoDaSintese.Validar(proposta, Trilha).Sintese!;
        using var documento = JsonDocument.Parse(sintese.Conteudo);
        Assert.Equal(1, documento.RootElement.GetProperty("conclusoes")[0].GetProperty("citacoes").GetArrayLength());
    }

    [Fact]
    public void TrilhaVaziaNaoTemOQueSintetizar() =>
        Assert.Throws<ArgumentException>(() => ValidacaoDaSintese.Validar(Valida(), []));

    // A etapa de síntese é um turno do coordenador congelado (papel e versão do snapshot), fora de plano; só ele.
    [Fact]
    public void EtapaDeSinteseSoDoCoordenadorDoSnapshot()
    {
        var c = new Cenario();
        var coordenador = c.Participantes.Single(p => p.Papel == "coord");
        var etapa = EtapaDaExecucao.DaSintese(c.Execucao, 5, c.Coordenacao, coordenador, Agora, AgentKind.Codex, "gpt", "high");
        Assert.Equal((TipoDeEtapa.TurnoDeAgente, "coord", coordenador.IdVersaoDoAgente, (Guid?)null, false),
            (etapa.Tipo, etapa.Papel, etapa.IdVersaoDoAgente, etapa.IdPlano, etapa.Escrita));
        Assert.Throws<InvalidOperationException>(() => EtapaDaExecucao.DaSintese(c.Execucao, 5, c.Coordenacao,
            c.Participantes.Single(p => p.Papel == "pesq"), Agora, AgentKind.Codex));
        var outra = new Cenario(51);
        Assert.Throws<InvalidOperationException>(() => EtapaDaExecucao.DaSintese(outra.Execucao, 5, c.Coordenacao,
            coordenador, Agora, AgentKind.Codex));
    }

    // A entidade nasce só da etapa de síntese em curso com a sessão vinculada, e grava o hash recalculado do conteúdo.
    [Fact]
    public void SinteseGravadaSoPelaEtapaDeSinteseEmCursoComSessaoVinculada()
    {
        var c = new Cenario();
        var etapa = EtapaDaExecucao.DaSintese(c.Execucao, 5, c.Coordenacao, c.Participantes.Single(p => p.Papel == "coord"),
            Agora, AgentKind.Codex);
        var validada = ValidacaoDaSintese.Validar(Valida(), Trilha).Sintese!;
        // Pendente e sem sessão: recusada.
        Assert.Throws<InvalidOperationException>(() => new SinteseDaCoordenacao(c.Execucao, etapa, validada, Agora));
        etapa.TornarPronta([]);
        etapa.Iniciar(Agora);
        Assert.Throws<InvalidOperationException>(() => new SinteseDaCoordenacao(c.Execucao, etapa, validada, Agora));
        var sessao = new SessaoDeAgente(7, AgentKind.Codex, "telegram", 1001, "{}", AgentPermissionProfile.Plan, Agora,
            idExecucao: c.Execucao.Id);
        etapa.VincularSessao(sessao, null);
        var sintese = new SinteseDaCoordenacao(c.Execucao, etapa, validada with { Hash = new string('f', 64) }, Agora);
        Assert.Equal((validada.Hash, sessao.Id, etapa.Id), (sintese.Hash, sintese.IdSessaoDeAgente, sintese.IdEtapa));
        // O planejamento (turno do coordenador sem papel) não grava síntese.
        var planejamento = new EtapaDaExecucao(c.Execucao, 6, TipoDeEtapa.TurnoDeAgente, false, PoliticaDeRetomada.Automatica,
            Agora, AgentKind.Codex);
        planejamento.TornarPronta([]);
        planejamento.Iniciar(Agora);
        planejamento.VincularSessao(sessao, null);
        Assert.Throws<InvalidOperationException>(() => new SinteseDaCoordenacao(c.Execucao, planejamento, validada, Agora));
    }
}
