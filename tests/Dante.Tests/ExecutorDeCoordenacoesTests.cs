using Dante.Application.Agentes;
using Dante.Application.Execucoes;
using Dante.Domain.Agentes;
using Dante.Domain.Execucoes;
using Dante.Worker.Execucoes;

namespace Dante.Tests;

// #310 (AD-75), PR 2: o que o executor da coordenação entrega ao coordenador e como lê a proposta. A proposta é só o
// bloco JSON da última mensagem (a validação é do servidor); os desfechos de outros agentes entram como dado não
// confiável; o coordenador nunca recebe a si mesmo como papel.
public sealed class ExecutorDeCoordenacoesTests
{
    [Theory]
    [InlineData("Plano:\n```json\n{\"unidades\":[]}\n```", "{\"unidades\":[]}")]
    [InlineData("```\n{\"a\":1}\n```\ntexto\n```json\n{\"b\":{\"c\":2}}\n```", "{\"b\":{\"c\":2}}")]
    [InlineData("  {\"unidades\":[]}  ", "{\"unidades\":[]}")]
    [InlineData("Não consegui planejar.", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void PropostaEhOUltimoBlocoJsonOuAMensagemQueSoTemOObjeto(string? mensagem, string? esperado) =>
        Assert.Equal(esperado, ExecutorDeCoordenacoesDaFila.ExtrairJson(mensagem));

    [Fact]
    public void PromptInicialTemObjetivoPapeisOrcamentoESchemaSemOCoordenador()
    {
        var prompt = ExecutorDeCoordenacoesDaFila.Prompt(Planejamento(MotivoDoPlano.Inicial));
        Assert.Contains("revisão 1 do plano", prompt);
        Assert.Contains("Investigar o índice", prompt);
        Assert.Contains("- pesq: agente pesquisador v2; capacidades: Pesquisar; responsabilidades: Pesquisa.", prompt);
        Assert.DoesNotContain("- coord", prompt);
        Assert.Contains("no máximo 3 unidade(s)", prompt);
        Assert.Contains(ContratosDeDelegacao.Consulta.SchemaDeEntrada().ToJsonString(), prompt);
        Assert.Contains("Não envie hash", prompt);
        Assert.DoesNotContain("DADO_NAO_CONFIAVEL", prompt);
    }

    [Fact]
    public void RevisaoPorUnidadeSemRespostaEntregaOsDesfechosComoDadoNaoConfiavel()
    {
        var prompt = ExecutorDeCoordenacoesDaFila.Prompt(Planejamento(MotivoDoPlano.UnidadeSemResposta) with
        {
            Revisao = 2,
            PlanoAnterior = "{\"formato\":\"1\"}",
            UnidadesAnteriores =
            [
                new UnidadeAnteriorDto("indice", "pesq", EstadoDaEtapa.Falhou, "limite do provider", null),
                new UnidadeAnteriorDto("consulta", "pesq", EstadoDaEtapa.Concluida, null, "{\"resposta\":\"ok\"}")
            ]
        });
        var inicio = prompt.IndexOf("<<<DADO_NAO_CONFIAVEL", StringComparison.Ordinal);
        var fim = prompt.IndexOf("DADO_NAO_CONFIAVEL>>>", StringComparison.Ordinal);
        Assert.True(inicio > 0 && fim > inicio);
        var dado = prompt[inicio..fim];
        Assert.Contains("indice (pesq): Falhou; motivo: limite do provider", dado);
        Assert.Contains("consulta (pesq): Concluida; resultado: {\"resposta\":\"ok\"}", dado);
    }

    [Fact]
    public void RevisaoPedidaNoGateLevaAInstrucaoEOPlanoAnterior()
    {
        var prompt = ExecutorDeCoordenacoesDaFila.Prompt(Planejamento(MotivoDoPlano.PedidoHumano) with
        {
            Revisao = 2, PlanoAnterior = "{\"formato\":\"1\"}", InstrucaoHumana = "Inclua o índice parcial."
        });
        Assert.Contains("Instrução dela:\nInclua o índice parcial.", prompt);
        Assert.Contains("Plano anterior:\n{\"formato\":\"1\"}", prompt);
    }

    [Fact]
    public void CorrecaoListaCadaRecusaComAUnidade()
    {
        var texto = ExecutorDeCoordenacoesDaFila.Correcao(
        [
            new RecusaDoPlano(MotivoDaRecusaDoPlano.RequerGateway, "u1", "Escrita depende do gateway."),
            new RecusaDoPlano(MotivoDaRecusaDoPlano.Ciclo, null, "Há ciclo.")
        ]);
        Assert.Contains("- RequerGateway (u1): Escrita depende do gateway.", texto);
        Assert.Contains("- Ciclo: Há ciclo.", texto);
        Assert.Contains("uma vez", texto);
    }

    private static PlanejamentoDaCoordenacaoDto Planejamento(MotivoDoPlano motivo) => new(Guid.NewGuid(), "E7", "telegram", 1,
        "{}", "Investigar o índice", "pesquisa", 1, "coord", "coordenador", new VersaoDoAgenteDto(),
        [
            new PapelParaPlanejarDto("pesq", "pesquisador", 2, "Pesquisa.", [CapacidadeDoAgente.Pesquisar]),
            new PapelParaPlanejarDto("rev", "revisor", 1, null, [CapacidadeDoAgente.Revisar])
        ], 3, 1, motivo, null, [], null, 0);
}
