using System.Reflection;
using Dante.Application.Agentes;
using Dante.Domain.Agentes;
using Dante.Domain.Grupos;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Tests;

// #309 (AD-70): a delegação entra no JSON canônico da versão do agente só quando a versão a define (formato 2). Os
// valores esperados abaixo foram calculados pelo código de main antes da #309 (0da389d), com estas mesmas entradas: se
// um deles mudar, uma versão gravada antes deixaria de ser íntegra (e de ser resolvida para sessão ou execução).
public sealed class CompatibilidadeDosHashesTests
{
    private static readonly DateTimeOffset Agora = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    internal static ConfiguracaoDoAgente Minima() =>
        new(AgentKind.Codex, AgentPermissionProfile.Plan, AgentPermissionProfile.Plan, false);

    internal static ConfiguracaoDoAgente Completa() =>
        new(AgentKind.Codex, AgentPermissionProfile.Auto, AgentPermissionProfile.Auto, true, "gpt-5.5", "high",
            "Revise com cuidado.\nSem pressa.", [CapacidadeDoAgente.Revisar, CapacidadeDoAgente.Implementar],
            [FerramentaDoDante.Planilhas, FerramentaDoDante.Brain], ["@dante", "@outro"], new LimitesDoAgente(2, 1000, 5, 30));

    internal const string HashDaMinima = "1fbb0e6f4213c04631d26183fa2cdf63aecc28d4a837e714a77ccd4f58ffcbf8";
    internal const string HashDaCompleta = "accd6dd2de409be3428832feec23330b651c938a73a15cd62ec9c174b4ef54b6";
    internal const string HashDoGrupo = "bb021a4456971a1a68d7e28044428638e52f7a8756a0ca7ca76b048df2185499";

    internal static ConfiguracaoDoGrupo Grupo() => new(ModoDeCoordenacao.Roteiro,
        [
            new ParticipanteDoGrupo(Guid.Parse("11111111-1111-1111-1111-111111111111"), "dev", "implementa", true),
            new ParticipanteDoGrupo(Guid.Parse("22222222-2222-2222-2222-222222222222"), "rev", null, false)
        ],
        [
            new PassoDoRoteiro(TipoDePassoDoRoteiro.Implementar, "dev"), new PassoDoRoteiro(TipoDePassoDoRoteiro.Revisar, "rev"),
            new PassoDoRoteiro(TipoDePassoDoRoteiro.Corrigir, "dev"), new PassoDoRoteiro(TipoDePassoDoRoteiro.AprovacaoHumana)
        ],
        new PoliticaDoGrupo(2, 1, ["@dante"], 90));

    [Fact]
    public void VersoesSemDelegacaoMantemOFormatoEOHashAnteriores()
    {
        Assert.Equal(
            """{"capacidades":[],"driver":"Codex","esforco":null,"ferramentas":[],"formato":"1","instrucoes":null,"limites":{"execucoesSimultaneas":null,"minutosPorEtapa":null,"tokensPorExecucao":null,"turnosPorEtapa":null},"modelo":null,"perfilMaximo":"Plan","perfilPadrao":"Plan","podeEscrever":false,"repositorios":[]}""",
            Minima().JsonCanonico());
        Assert.Equal(HashDaMinima, Minima().Hash());
        Assert.Equal(HashDaCompleta, Completa().Hash());
        Assert.Equal("cf5744ac4a1286079adc357a015e8ab69e33cdb204b65bdb8c02b2377d1abd14",
            new ConfiguracaoDoAgente(AgentKind.Claude, AgentPermissionProfile.Manual, AgentPermissionProfile.Auto, true,
                capacidades: [CapacidadeDoAgente.Pesquisar], repositorios: ["*"]).Hash());
    }

    [Fact]
    public void EmbutidosNaoGanhamCoordenacaoEMantemOHash()
    {
        var codex = AgentesEmbutidos.Materializar(AgentKind.Codex, null, AgentPermissionProfile.Plan);
        Assert.DoesNotContain(FerramentaDoDante.Coordenacao, codex.Ferramentas);
        Assert.Empty(codex.PodeConsultar);
        Assert.Equal("17dc78d166cf81dd55dc1ab4bf0c1b96d6abbf567704afc69e04ced7c0f79b5a", codex.Hash());
    }

    [Fact]
    public void GruposNaoMudamDeHash() => Assert.Equal(HashDoGrupo, Grupo().Hash());

    [Fact]
    public void ConcessaoDeCoordenacaoSemDelegacaoContinuaNoFormato1()
    {
        var configuracao = new ConfiguracaoDoAgente(AgentKind.Codex, AgentPermissionProfile.Plan, AgentPermissionProfile.Plan,
            false, ferramentas: [FerramentaDoDante.Coordenacao]);
        Assert.Contains("\"formato\":\"1\"", configuracao.JsonCanonico());
        Assert.DoesNotContain("delegacao", configuracao.JsonCanonico());
    }

    [Fact]
    public void VersaoComDelegacaoUsaOFormato2ComHashDeterministico()
    {
        ConfiguracaoDoAgente Com(IEnumerable<string> consulta, LimitesDeDelegacao? limites = null) =>
            new(AgentKind.Codex, AgentPermissionProfile.Plan, AgentPermissionProfile.Plan, false,
                ferramentas: [FerramentaDoDante.Coordenacao, FerramentaDoDante.Brain], podeConsultar: consulta,
                limitesDeDelegacao: limites);

        var a = Com(["revisor", "auditor"]);
        var b = Com([" Auditor", "revisor", "auditor"]);
        Assert.Equal(a.Hash(), b.Hash());
        Assert.Equal(
            """{"capacidades":[],"delegacao":{"limites":{},"podeConsultar":["auditor","revisor"]},"driver":"Codex","esforco":null,"ferramentas":["Brain","Coordenacao"],"formato":"2","instrucoes":null,"limites":{"execucoesSimultaneas":null,"minutosPorEtapa":null,"tokensPorExecucao":null,"turnosPorEtapa":null},"modelo":null,"perfilMaximo":"Plan","perfilPadrao":"Plan","podeEscrever":false,"repositorios":[]}""",
            a.JsonCanonico());
        var limitada = Com(["revisor", "auditor"], new LimitesDeDelegacao(1, 2, 10, 20, 1));
        Assert.Contains(
            "\"delegacao\":{\"limites\":{\"delegacoesPorExecucao\":2,\"minutosDeDuracao\":20,\"minutosParaIniciar\":10,\"paralelismo\":1,\"profundidadeMaxima\":1}",
            limitada.JsonCanonico());
        // Explicitar o teto não é o mesmo que não ter limite próprio: são versões diferentes.
        Assert.NotEqual(a.Hash(), Com(["revisor", "auditor"], LimitesDeDelegacao.Teto).Hash());
        Assert.NotEqual(a.Hash(), limitada.Hash());
        // Só limites, sem consultados, já é delegação definida (formato 2).
        Assert.Contains("\"formato\":\"2\"", Com([], LimitesDeDelegacao.Teto).JsonCanonico());
        Assert.Equal(LimitesDeDelegacao.Teto, a.LimitesEfetivosDeDelegacao);
    }

    [Fact]
    public void VersaoGravadaReproduzOHashEAdulteracaoEhDetectada()
    {
        var agente = new Agente("pesquisador", null, null, 1, Agora);
        var antiga = agente.NovaVersao(Completa(), 1, Agora);
        Assert.True(antiga.Integra);
        Assert.Equal(HashDaCompleta, antiga.HashDaConfiguracao);

        var nova = agente.NovaVersao(new ConfiguracaoDoAgente(AgentKind.Codex, AgentPermissionProfile.Plan,
            AgentPermissionProfile.Plan, false, ferramentas: [FerramentaDoDante.Coordenacao], podeConsultar: ["revisor"],
            limitesDeDelegacao: new LimitesDeDelegacao(1, 3, 15, 45, 1)), 1, Agora);
        Assert.True(nova.Integra);
        Assert.Equal(["revisor"], nova.PodeConsultar);
        Assert.Equal((1, 3, 15, 45, 1), (nova.ProfundidadeMaximaDeDelegacao!.Value, nova.DelegacoesPorExecucao!.Value,
            nova.MinutosParaIniciarDelegacao!.Value, nova.MinutosDeDelegacao!.Value, nova.ParalelismoDeDelegacoes!.Value));

        // Adulterações nas colunas da delegação: a versão deixa de ser íntegra.
        Alterar(nova, nameof(VersaoDoAgente.PodeConsultar), new[] { "revisor", "outro" });
        Assert.False(nova.Integra);
        Alterar(nova, nameof(VersaoDoAgente.PodeConsultar), new[] { "revisor" });
        Alterar(nova, nameof(VersaoDoAgente.DelegacoesPorExecucao), (int?)4);
        Assert.False(nova.Integra);
        // E numa versão antiga, passar a ter delegação também muda o hash (formato 2): adulteração detectada.
        Alterar(antiga, nameof(VersaoDoAgente.PodeConsultar), new[] { "revisor" });
        Assert.False(antiga.Integra);
    }

    // O cadastro ainda não edita a delegação (é da entrega que expõe a ferramenta): uma edição de outro campo herda a
    // da versão atual, sem perdê-la em silêncio.
    [Fact]
    public async Task EdicaoPeloCadastroPreservaADelegacaoDaVersaoAtual()
    {
        var portas = new CadastroDeAgentesEmMemoria();
        await using var provider = portas.Provider(new CatalogoDeModelosFixo());
        var agente = new Agente("pesquisador", null, null, 1, Agora);
        var versao = agente.NovaVersao(new ConfiguracaoDoAgente(AgentKind.Claude, AgentPermissionProfile.Plan,
            AgentPermissionProfile.Plan, false, ferramentas: [FerramentaDoDante.Coordenacao], podeConsultar: ["revisor"],
            limitesDeDelegacao: new LimitesDeDelegacao(1, 2, 10, 20, 1)), 1, Agora);
        await portas.AdicionarAsync(agente);
        await portas.AdicionarAsync(versao);
        await portas.SalvarAlteracoesAsync();

        var editado = await provider.GetRequiredService<ICadastroDeAgentesAppService>().EditarAsync("pesquisador",
            new AlteracaoDoAgenteDto { Instrucoes = "Pesquise com fontes." }, 1);
        var configuracao = editado.VersaoAtual!.Configuracao;
        Assert.Equal(2, editado.NumeroDaVersaoAtual);
        Assert.Equal(["revisor"], configuracao.PodeConsultar);
        Assert.Equal(new LimitesDeDelegacao(1, 2, 10, 20, 1), configuracao.LimitesDeDelegacao);
        Assert.True(portas.Versoes[^1].Integra);
    }

    private static void Alterar(VersaoDoAgente versao, string propriedade, object? valor) =>
        typeof(VersaoDoAgente).GetProperty(propriedade, BindingFlags.Public | BindingFlags.Instance)!.SetValue(versao,
            valor is string[] lista ? Array.AsReadOnly(lista) : valor);
}
