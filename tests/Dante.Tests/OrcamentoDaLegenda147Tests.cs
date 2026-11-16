using System.Text.Json;
using Dante.Application.ConstrucaoDeContexto;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;
using Dante.Domain.RelacoesDeConhecimento;

namespace Dante.Tests;

// Orçamento com fonte omitida (#147, revisão da PR #295): a forma final de uma fonte coberta (chave Omitida, lista
// ConsolidadaEm e a linha de explicação no cabeçalho) pode custar mais que a íntegra quando a fonte é curta. A seleção
// tem de contar o maior dos dois custos, e a explicação do cabeçalho uma vez, para que nenhum item entre com custo
// subestimado. Cada cenário percorre todos os orçamentos, do cabeçalho vazio à folga total.
public sealed class OrcamentoDaLegenda147Tests
{
    private static ItemDeContextoDto Conhecimento(string conteudo, StatusDoConhecimento status = StatusDoConhecimento.Inferido,
        NaturezaDoConteudo origem = NaturezaDoConteudo.ConclusaoDoAgente, TipoDeConhecimento tipo = TipoDeConhecimento.Fato)
    {
        var id = Guid.NewGuid();
        return new($"conhecimento:{id:D}", id, 1, "conhecimento", tipo, status, Sensibilidade.Pessoal, conteudo, $"conhecimento:{id:D}@1", "teste", 1, 0,
            NaturezaDeOrigem: origem, Natureza: origem);
    }
    private static ItemDeContextoDto Fonte(string conteudo)
    {
        var id = Guid.NewGuid();
        return new($"fonte:{id:D}", id, 1, "fonte_bruta", null, null, Sensibilidade.Pessoal, conteudo, $"fonte:{id:D}@1", "teste", 0, 0,
            AutoriaDaFonte: AutoriaDaFonte.Agente, Titulo: "Doc " + id.ToString("N")[..4]);
    }
    // Fonte curta coberta por dois conhecimentos que citam as suas linhas: a forma omitida é maior que a íntegra.
    private static (ItemDeContextoDto Fonte, ItemDeContextoDto[] Derivados) Coberta(string nome)
    {
        var a = Conhecimento($"# {nome}\nlinha um"); var b = Conhecimento($"linha dois de {nome}");
        return (Fonte($"# {nome}\nlinha um\nlinha dois de {nome}"), [a, b]);
    }

    private static PacoteDeContextoDto Selecionar(int orcamento, IReadOnlyList<ItemDeContextoDto> itens,
        IReadOnlyDictionary<string, IReadOnlyList<string>> provas, IReadOnlyList<RelacaoEntreItens>? relacoes = null) =>
        SelecaoDeContexto.Selecionar(new() { Mensagem = "pedido", OrcamentoDeTokens = orcamento, LimiteDeItens = 50 }, itens, [], [], false, relacoes, provas);

    // Varre do cabeçalho mínimo até sobrar orçamento; em cada ponto, o pacote cabe e é o que foi contabilizado.
    private static List<PacoteDeContextoDto> Varrer(IReadOnlyList<ItemDeContextoDto> itens, IReadOnlyDictionary<string, IReadOnlyList<string>> provas,
        IReadOnlyList<RelacaoEntreItens>? relacoes = null)
    {
        var folga = Selecionar(100000, itens, provas, relacoes);
        Assert.Equal(itens.Count, folga.Itens.Count);
        int Relacoes(PacoteDeContextoDto p) => p.Itens.Sum(x => x.Relacoes?.Count ?? 0);
        var pacotes = new List<PacoteDeContextoDto>(); int? completoEm = null;
        // Até 20 tokens além do primeiro orçamento em que tudo entra (itens e relações), como na folga.
        for (var orcamento = SelecaoDeContexto.EstimarTokens(FormatoDeContexto.Cabecalho(PlanoDoFormato.Extenso)); completoEm is null || orcamento <= completoEm + 20; orcamento++)
        {
            var pacote = Selecionar(orcamento, itens, provas, relacoes);
            AssertContabilizado(pacote, orcamento);
            pacotes.Add(pacote);
            if (completoEm is null && pacote.Itens.Count == itens.Count && Relacoes(pacote) == Relacoes(folga))
            {
                completoEm = orcamento;
                // Mesmo texto da folga: o orçamento só decide o que entra, não a forma.
                Assert.Equal(folga.TextoParaInjecao.Length, pacote.TextoParaInjecao.Length);
            }
            Assert.True(orcamento < folga.Custo.TokensEstimados * 2, "a seleção nunca admite tudo");
        }
        return pacotes;
    }
    private static void AssertContabilizado(PacoteDeContextoDto pacote, int orcamento)
    {
        Assert.True(pacote.Custo.TokensEstimados <= orcamento, $"pacote {pacote.Custo.TokensEstimados} > orçamento {orcamento}");
        Assert.Equal(orcamento, pacote.Custo.Orcamento);
        if (pacote.Itens.Count == 0) return;
        // O custo registrado é o do texto entregue, e o texto é exatamente os itens (com as suas relações) no plano final.
        Assert.Equal(SelecaoDeContexto.EstimarTokens(pacote.TextoParaInjecao), pacote.Custo.TokensEstimados);
        Assert.Equal(pacote.Itens.Count, pacote.Custo.Itens);
        Assert.Equal(FormatoDeContexto.Texto(pacote.Itens, FormatoDeContexto.Planejar(pacote.Itens)), pacote.TextoParaInjecao);
        var linhas = pacote.TextoParaInjecao.Split('\n').Where(l => l.StartsWith('{')).Select(l => JsonDocument.Parse(l).RootElement).ToArray();
        Assert.Equal(pacote.Itens.Count, linhas.Length);
        var refs = linhas.Select(l => l.GetProperty("Ref").GetString()!).ToHashSet(StringComparer.Ordinal);
        foreach (var (item, linha) in pacote.Itens.Zip(linhas))
        {
            // Relação só aponta item presente; fonte omitida só com a cobertura inteira no pacote.
            foreach (var r in item.Relacoes ?? []) Assert.Contains(r[(r.IndexOf(' ') + 1)..], refs);
            if (item.ConsolidadaEm is { Count: > 0 } c) Assert.All(c, k => Assert.Contains(k, refs));
            Assert.Equal(item.ConsolidadaEm is { Count: > 0 }, linha.TryGetProperty("Omitida", out _));
        }
        // Registro do item selecionado = custo da sua linha no texto final.
        var porChave = pacote.Itens.ToDictionary(x => x.Chave, StringComparer.Ordinal);
        Assert.All(pacote.Registros.Where(r => r.Estado == "selecionado"), r => Assert.Equal(porChave[r.Chave].TokensEstimados, r.TokensEstimados));
    }
    private static Dictionary<string, IReadOnlyList<string>> Provas(params (ItemDeContextoDto Fonte, ItemDeContextoDto[] Derivados)[] cobertas) =>
        cobertas.ToDictionary(c => c.Fonte.Chave, c => (IReadOnlyList<string>)c.Derivados.Select(d => d.Chave).ToArray());

    [Fact]
    public void UmaFonteOmitidaNuncaPassaDoOrcamento()
    {
        var c = Coberta("Um");
        var pacotes = Varrer([c.Fonte, .. c.Derivados], Provas(c));
        // A omissão ocorre na faixa varrida: o caso exercita a forma que a seleção antes subestimava.
        Assert.Contains(pacotes, p => p.Itens.Any(x => x.ConsolidadaEm is { Count: > 0 }));
    }

    [Fact]
    public void MultiplasFontesOmitidasContamAExplicacaoDoCabecalhoUmaVez()
    {
        var um = Coberta("Um"); var dois = Coberta("Dois"); var tres = Coberta("Tres");
        var pacotes = Varrer([um.Fonte, dois.Fonte, tres.Fonte, .. um.Derivados, .. dois.Derivados, .. tres.Derivados], Provas(um, dois, tres));
        Assert.Contains(pacotes, p => p.Itens.Count(x => x.ConsolidadaEm is { Count: > 0 }) == 3);
        Assert.All(pacotes.Where(p => p.Itens.Any(x => x.ConsolidadaEm is { Count: > 0 })),
            p => Assert.Equal(1, p.TextoParaInjecao.Split(FormatoDeContexto.Omitido).Length - 1));
    }

    [Fact]
    public void FonteOmitidaComConhecimentosEFontesNaoOmitidos()
    {
        var c = Coberta("Um");
        var decisao = Conhecimento("Decisão do usuário: manter o banco canônico intocado.", StatusDoConhecimento.Confirmado,
            NaturezaDoConteudo.DitoPeloUsuario, TipoDeConhecimento.Decisao);
        var solta = Conhecimento("Conclusão sem fonte associada.");
        var integral = Fonte("# Outra\nsem conhecimento que a cubra");
        var pacotes = Varrer([c.Fonte, integral, decisao, solta, .. c.Derivados], Provas(c));
        var completo = pacotes[^1];
        Assert.Single(completo.Itens, x => x.ConsolidadaEm is { Count: > 0 });
        Assert.Contains(completo.Itens, x => x.Id == integral.Id && x.ConsolidadaEm is null);
    }

    [Fact]
    public void RelacoesEntramOuSaemNoLimiteJuntoDaFonteOmitida()
    {
        var c = Coberta("Caso");
        var (incidente, solucao) = (c.Derivados[0], c.Derivados[1]);
        var x = Conhecimento("Incidente sem fonte: worker reiniciado fora de hora."); var y = Conhecimento("Solução: agendar a janela de manutenção.");
        var z = Conhecimento("Restrição: não reiniciar durante lote de medição.");
        var relacoes = new[]
        {
            new RelacaoEntreItens(incidente.Id, solucao.Id, TipoDeRelacao.ResolvidoPor), new RelacaoEntreItens(x.Id, y.Id, TipoDeRelacao.ResolvidoPor),
            new RelacaoEntreItens(x.Id, z.Id, TipoDeRelacao.RelacionadoA), new RelacaoEntreItens(y.Id, z.Id, TipoDeRelacao.DependeDe),
        };
        var pacotes = Varrer([c.Fonte, .. c.Derivados, x, y, z], Provas(c), relacoes);
        int Relacoes(PacoteDeContextoDto p) => p.Itens.Sum(i => i.Relacoes?.Count ?? 0);
        bool Completo(PacoteDeContextoDto p) => p.Itens.Count == 6 && p.Itens.Any(i => i.ConsolidadaEm is { Count: > 0 });
        // No limite: o mesmo conjunto de itens, com a fonte omitida, cabe com parte das relações e depois com todas.
        var completos = pacotes.Where(Completo).ToArray();
        Assert.Contains(completos, p => Relacoes(p) < relacoes.Length);
        Assert.Contains(completos, p => Relacoes(p) == relacoes.Length);
        // Cada relação que sai do pacote é a que não coube: com mais orçamento, nenhuma relação já presente sai.
        for (var i = 1; i < completos.Length; i++) Assert.True(Relacoes(completos[i]) >= Relacoes(completos[i - 1]));
        Assert.Equal([$"ResolvidoPor conhecimento:{solucao.Id:D}@1"], completos[^1].Itens.Single(i => i.Id == incidente.Id).Relacoes);
    }
}
