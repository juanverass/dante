using Dante.Application.BuscaDoBrain;
using Dante.Application.ConstrucaoDeContexto;
using Dante.Application.MetricasDoBrain;
using Dante.Application.QualidadeDoBrain;
using Dante.Domain.Conhecimentos;
using Dante.Domain.RelacoesDeConhecimento;

namespace Dante.Tests;

public sealed class PoliticasDoBrainTests
{
    private static readonly DateTimeOffset Agora = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Espaco = Guid.NewGuid(), Usuario = Guid.NewGuid();

    [Fact]
    public void AnaliseDistingueDuplicataDeContradicaoSemAlterarDominio()
    {
        var a = Conhecimento("usar ordem consistente de locks");
        var b = Conhecimento("usar  ordem consistente de locks");
        var negado = Conhecimento("não usar ordem consistente de locks");
        var relatorio = AnaliseDeQualidade.Analisar([a, b, negado], [], Agora, null, false);
        Assert.Contains(relatorio.Achados, x => x.Problema == ProblemaDeQualidade.PossivelDuplicata && x.IdConhecimento == a.Id);
        Assert.Contains(relatorio.Achados, x => x.Problema == ProblemaDeQualidade.PossivelContradicao);
        Assert.All(new[] { a, b, negado }, x => { Assert.Equal(1, x.Revisao); Assert.Equal(StatusDoConhecimento.Inferido, x.Status); });
    }

    [Fact]
    public void AnaliseEstruturadaPriorizaContradicaoEReportaConflitoExplicito()
    {
        var a = Conhecimento("mesmo texto", "{\"chave\":\"banco\",\"valor\":\"a\"}");
        var b = Conhecimento("mesmo texto", "{\"chave\":\"banco\",\"valor\":\"b\"}");
        var r = new RelacaoDeConhecimento(a, b, TipoDeRelacao.Contradiz, a.Proveniencia, Agora);
        var relatorio = AnaliseDeQualidade.Analisar([a, b], [r], Agora, null, false);
        Assert.Contains(relatorio.Achados, x => x.Problema == ProblemaDeQualidade.PossivelContradicao);
        Assert.DoesNotContain(relatorio.Achados, x => x.Problema == ProblemaDeQualidade.PossivelDuplicata);
        Assert.Contains(relatorio.Achados, x => x.Problema == ProblemaDeQualidade.ContradicaoExplicita);
        Assert.Null(Assert.Single(relatorio.Conflitos).ResolvidoEm);
    }

    [Fact]
    public void AnaliseNaoDeclaraOrfaoQuandoConsultaDeRelacoesTruncou()
    {
        var a = Conhecimento("a"); var b = Conhecimento("b"); var fora = Conhecimento("fora da vizinhança");
        var r = new RelacaoDeConhecimento(a, b, TipoDeRelacao.RelacionadoA, a.Proveniencia, Agora);
        var relatorio = AnaliseDeQualidade.Analisar([fora], Enumerable.Repeat(r, 1001).ToArray(), Agora, null, false);
        Assert.True(relatorio.LimiteAtingido);
        Assert.DoesNotContain(relatorio.Achados, x => x.Problema == ProblemaDeQualidade.Orfao);
    }

    [Fact]
    public void AnaliseDeValidadeUsaInstanteRecebidoERevisaoDaConfirmacao()
    {
        var a = Conhecimento("confirmado"); a.Confirmar(1, a.Proveniencia, Agora);
        var expira = Conhecimento("expirado");
        expira.Corrigir(1, expira.Tipo, expira.Conteudo, null, null, expira.Sensibilidade,
            null, Agora.AddMinutes(1), [], expira.Proveniencia, Agora);
        var relatorio = AnaliseDeQualidade.Analisar([a, expira], [], Agora.AddHours(1), Agora.AddMinutes(1), true);
        Assert.True(relatorio.LimiteAtingido);
        Assert.Contains(relatorio.Achados, x => x.Problema == ProblemaDeQualidade.ForaDaValidade && x.IdConhecimento == expira.Id);
        Assert.Contains(relatorio.Achados, x => x.Problema == ProblemaDeQualidade.ConfirmacaoAnteriorAoLimite && x.IdConhecimento == a.Id);
    }

    [Fact]
    public void SelecaoPriorizaInstrucaoConfirmadaAntesDeRelevancia()
    {
        var nota = Item("a", "nota relevante", relevancia: 100);
        var instrucao = Item("z", "instrução confirmada", tipo: TipoDeConhecimento.Instrucao, status: StatusDoConhecimento.Confirmado);
        var pacote = Selecionar(new() { Mensagem = "pedido", LimiteDeItens = 1 }, [nota, instrucao]);
        Assert.Equal(instrucao.Id, Assert.Single(pacote.Itens).Id);
        Assert.Contains(pacote.Registros, x => x.Id == nota.Id && x.Motivo == "Limite de itens.");
    }

    [Fact]
    public void SelecaoDeduplicaResumoItensERevisaoInjetada()
    {
        var presente = Item("presente", "conteúdo já citado no resumo");
        var duplicado = Item("duplicado", "conteúdo repetido entre candidatos");
        var repetido = duplicado with { Id = Guid.NewGuid(), Chave = "repetido" };
        var injetado = Item("injetado", "já enviado anteriormente");
        var revisado = Item("revisado", "nova revisão aceita") with { Revisao = 2 };
        var pedido = new PedidoDeContextoDto { Mensagem = "pedido", FragmentosJaPresentes = [presente.Conteudo],
            JaInjetados = new Dictionary<string, int> { [injetado.Chave] = 1, [revisado.Chave] = 1 } };
        var pacote = Selecionar(pedido, [presente, duplicado, repetido, injetado, revisado]);
        Assert.Equal(2, pacote.Itens.Count);
        Assert.Contains(pacote.Itens, x => x.Id == revisado.Id);
        Assert.Contains(pacote.Registros, x => x.Id == injetado.Id && x.Motivo.Contains("Já injetado"));
        Assert.DoesNotContain(pacote.Itens, x => x.Id == presente.Id);
    }

    [Fact]
    public void SelecaoLimitaOrcamentoSemImpedirItemMenorPosterior()
    {
        var grande = Item("a", new string('x', 3000), relevancia: 100);
        var pequeno = Item("b", "curto");
        var pacote = Selecionar(new() { Mensagem = "pedido", OrcamentoDeTokens = 300 }, [grande, pequeno]);
        Assert.Equal(pequeno.Id, Assert.Single(pacote.Itens).Id);
        Assert.InRange(pacote.Custo.TokensEstimados, 1, 300);
        Assert.Contains(pacote.Registros, x => x.Id == grande.Id && x.Motivo.Contains("orçamento"));
        Assert.Equal(ConstrutorDeContextoAppService.EstimarTokens(pacote.TextoParaInjecao), pacote.Custo.TokensEstimados);
    }

    [Fact]
    public void SobreposicaoUsaRunesUnicodeEDistingueRevisoes()
    {
        var id = Guid.NewGuid();
        var primeiro = Item("a", "😀😀😀", "fonte_bruta") with { Id = id, InicioDaFonte = 0 };
        var sobreposto = Item("b", "😀abc", "fonte_bruta") with { Id = id, InicioDaFonte = 2 };
        var adjacente = Item("c", "def", "fonte_bruta") with { Id = id, InicioDaFonte = 3 };
        var revisado = sobreposto with { Chave = "d", Revisao = 2, Conteudo = "nova" };
        var pacote = Selecionar(new() { Mensagem = "pedido" }, [primeiro, sobreposto, adjacente, revisado]);
        Assert.Equal(3, pacote.Itens.Count);
        Assert.Contains(pacote.Registros, x => x.Chave == "b" && x.Motivo.Contains("sobreposto"));
    }

    [Fact]
    public void SnapshotRemoveCamposJaSelecionadosEMantemOutros()
    {
        var conhecimento = Item("a", "decisão confirmada", status: StatusDoConhecimento.Confirmado);
        var snapshot = Item("snapshot", "decisão confirmada\ntarefa atual", "snapshot");
        var pacote = SelecaoDeContexto.Selecionar(new() { Mensagem = "pedido" }, [conhecimento, snapshot],
            ["decisão confirmada", "tarefa atual"], [], false);
        Assert.Equal("tarefa atual", Assert.Single(pacote.Itens, x => x.Origem == "snapshot").Conteudo);
        var vazio = Selecionar(new() { Mensagem = "pedido" }, []);
        Assert.Empty(vazio.TextoParaInjecao); Assert.Equal(0, vazio.Custo.TokensEstimados);
    }

    [Fact]
    public void ElegibilidadeRespeitaTagsTipoStatusEDataFinalExclusiva()
    {
        var k = Conhecimento("regra");
        Assert.True(ElegibilidadeDeContexto.AtendeFiltros(k, new() { CriadoDesde = Agora }));
        Assert.False(ElegibilidadeDeContexto.AtendeFiltros(k, new() { CriadoAte = Agora }));
        Assert.False(ElegibilidadeDeContexto.AtendeFiltros(k, new() { Tags = ["inexistente"] }));
        Assert.False(ElegibilidadeDeContexto.AtendeFiltros(k, new() { Tipo = TipoDeConhecimento.Decisao }));
        Assert.False(ElegibilidadeDeContexto.AtendeFiltros(k, new() { Status = StatusDoConhecimento.Confirmado }));
    }

    [Fact]
    public void AgregacaoOrdenaEventosEPreservaAusenciaEAvaliacaoMaisRecente()
    {
        var envio = new MetricaDoBrainDto { Tipo = MetricaDoBrainDto.Envio, Em = Agora, IdSessao = "a", TokensDoPedido = 100 };
        var turno = new MetricaDoBrainDto { Tipo = MetricaDoBrainDto.Turno, Em = Agora.AddMinutes(1), IdSessao = "a", TokensDaResposta = 900 };
        var retomada = envio with { Em = Agora.AddHours(1), IdSessao = "b", Injetados = 1, TokensDoPacote = 500 };
        var avaliacao = new MetricaDoBrainDto { Tipo = MetricaDoBrainDto.Avaliacao, Em = Agora.AddHours(2), IdSessao = "b", Relevantes = 2 };
        var ultima = avaliacao with { Em = Agora.AddHours(3), Relevantes = 3, Irrelevantes = 1 };
        var resumo = AgregacaoDeMetricas.Resumir([ultima, retomada, avaliacao, turno, envio]);
        Assert.Equal("ganho", resumo.Indicacao); Assert.Equal(0.75, resumo.Precisao);
        Assert.Equal(ultima, Assert.Single(resumo.Retomadas).Avaliacao);
        Assert.Null(resumo.EntradaReportada); Assert.Null(resumo.TokensMediosDoSnapshot);
        Assert.Equal(1, resumo.TurnosSemUsoReportado);
    }

    [Fact]
    public void SelecaoPreservaDiagnosticosAnterioresSemModificarEntrada()
    {
        var registro = new RegistroDeContextoDto("ausente", Guid.NewGuid(), "conhecimento", "descartado", "fora do escopo", 0);
        var registros = new List<RegistroDeContextoDto> { registro };
        var pacote = SelecaoDeContexto.Selecionar(new() { Mensagem = "pedido" }, [Item("novo", "conteúdo novo")], [], registros, true);
        Assert.Single(registros);
        Assert.Equal(registro, pacote.Registros[0]);
        Assert.Equal(2, pacote.Registros.Count);
        Assert.True(pacote.RecuperacaoLimitada);
    }

    private static Conhecimento Conhecimento(string conteudo, string? json = null) => new(Espaco, null,
        TipoDeConhecimento.Fato, conteudo, json, StatusDoConhecimento.Inferido, null, Sensibilidade.Pessoal,
        null, null, [], new(Usuario, "manual", "fonte:teste"), Agora);
    private static ItemDeContextoDto Item(string chave, string conteudo, string origem = "conhecimento", double relevancia = 1,
        TipoDeConhecimento? tipo = TipoDeConhecimento.Fato, StatusDoConhecimento? status = StatusDoConhecimento.Inferido) =>
        new(chave, Guid.NewGuid(), 1, origem, tipo, status, Sensibilidade.Pessoal, conteudo, null, "teste", relevancia, 0);
    private static PacoteDeContextoDto Selecionar(PedidoDeContextoDto pedido, IReadOnlyList<ItemDeContextoDto> itens) =>
        SelecaoDeContexto.Selecionar(pedido, itens, [], [], false);
}
