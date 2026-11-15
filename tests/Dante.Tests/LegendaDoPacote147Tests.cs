using System.Text;
using System.Text.Json;
using Dante.Application.ConstrucaoDeContexto;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;

namespace Dante.Tests;

// Legenda do pacote (#147): O1 rótulo repetido no cabeçalho, O2 ConsolidadaEm implícita quando cobre todos os
// conhecimentos do pacote, O3 explicação da fonte omitida uma vez no cabeçalho com a chave Omitida na linha.
// Cada forma é sem perda: um leitor que só aplica as regras declaradas no cabeçalho recupera exatamente o rótulo, a
// sensibilidade, a omissão e a cobertura de cada item do DTO. E o texto nunca cresce em relação à forma extensa.
public sealed class LegendaDoPacote147Tests
{
    private static ItemDeContextoDto Conhecimento(string conteudo, StatusDoConhecimento status = StatusDoConhecimento.Inferido,
        NaturezaDoConteudo origem = NaturezaDoConteudo.ConclusaoDoAgente, NaturezaDoConteudo? natureza = null, TipoDeConhecimento tipo = TipoDeConhecimento.Fato)
    {
        var id = Guid.NewGuid();
        return new($"conhecimento:{id:D}", id, 1, "conhecimento", tipo, status, Sensibilidade.Pessoal, conteudo, $"conhecimento:{id:D}@1", "teste", 1, 0,
            NaturezaDeOrigem: origem, Natureza: natureza ?? origem);
    }
    private static ItemDeContextoDto Fonte(string conteudo, AutoriaDaFonte autoria = AutoriaDaFonte.Agente)
    {
        var id = Guid.NewGuid();
        return new($"fonte:{id:D}", id, 1, "fonte_bruta", null, null, Sensibilidade.Pessoal, conteudo, $"fonte:{id:D}@1", "teste", 0, 0, AutoriaDaFonte: autoria, Titulo: "Doc " + id.ToString("N")[..4]);
    }
    private static PacoteDeContextoDto Selecionar(IReadOnlyList<ItemDeContextoDto> itens, Dictionary<string, IReadOnlyList<string>>? provas = null) =>
        SelecaoDeContexto.Selecionar(new() { Mensagem = "pedido", OrcamentoDeTokens = 100000, LimiteDeItens = 50 }, itens, [], [], false, [], provas);

    // ---------- Leitor independente: aplica só o que o cabeçalho declara ----------
    private sealed record Lido(string Ref, string Rotulo, string Sensibilidade, bool Omitida, string[]? ConsolidadaEm, string? Conteudo);
    private static Lido[] Ler(string texto)
    {
        var linhas = texto.Split('\n');
        var cabecalho = linhas.TakeWhile(l => !l.StartsWith('{')).ToArray();
        var rotulos = new Dictionary<string, string>(StringComparer.Ordinal);
        var legenda = cabecalho.SingleOrDefault(l => l.StartsWith(FormatoDeContexto.RotulosNaLegenda, StringComparison.Ordinal));
        if (legenda is not null)
            foreach (var p in JsonDocument.Parse(legenda[(legenda.IndexOf("]: ", StringComparison.Ordinal) + 3)..]).RootElement.EnumerateObject()) rotulos[p.Name] = p.Value.GetString()!;
        var uniforme = System.Text.RegularExpressions.Regex.Match(cabecalho[1], @" Sensibilidade=(\w+) em todos\.$");
        var implicita = cabecalho.Any(l => l == FormatoDeContexto.Omitido + FormatoDeContexto.OmitidoImplicito);
        var itens = linhas.Where(l => l.StartsWith('{')).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToArray();
        var conhecimentos = itens.Select(i => i.GetProperty("Ref").GetString()!).Where(r => r.StartsWith("conhecimento:", StringComparison.Ordinal)).ToArray();
        string? S(JsonElement e, string n) => e.TryGetProperty(n, out var v) ? v.GetString() : null;
        return itens.Select(i =>
        {
            var r = S(i, "Ref")!;
            var chave = r.StartsWith("conhecimento:", StringComparison.Ordinal) ? $"{S(i, "Status") ?? "-"}/{S(i, "NaturezaDeOrigem") ?? "-"}" + (S(i, "Natureza") is { } n ? "/" + n : "")
                : r.StartsWith("fonte:", StringComparison.Ordinal) ? "fonte/" + (S(i, "AutoriaDaFonte") ?? "-") : r[..r.IndexOf(':')];
            var omitida = i.TryGetProperty("Omitida", out var o) && o.GetBoolean();
            var lista = i.TryGetProperty("ConsolidadaEm", out var c) ? c.EnumerateArray().Select(x => x.GetString()!).ToArray() : omitida && implicita ? conhecimentos : null;
            return new Lido(r, S(i, "Rotulo") ?? rotulos[chave], S(i, "Sensibilidade") ?? uniforme.Groups[1].Value, omitida, lista, S(i, "Conteudo"));
        }).ToArray();
    }
    // O que o leitor recupera é o que o DTO tem: nada se perde nem muda de item.
    private static void AssertSemPerda(PacoteDeContextoDto pacote)
    {
        var lidos = Ler(pacote.TextoParaInjecao);
        Assert.Equal(pacote.Itens.Count, lidos.Length);
        foreach (var (x, l) in pacote.Itens.Zip(lidos))
        {
            Assert.Equal(x.Rotulo, l.Rotulo); Assert.Equal(x.Sensibilidade.ToString(), l.Sensibilidade);
            Assert.Equal(x.ConsolidadaEm is { Count: > 0 }, l.Omitida);
            if (x.ConsolidadaEm is { Count: > 0 } c) { Assert.Equal(c.Order(StringComparer.Ordinal), l.ConsolidadaEm!.Order(StringComparer.Ordinal)); Assert.Null(l.Conteudo); }
        }
        // Nunca maior que o formato anterior para os mesmos itens.
        Assert.True(Encoding.UTF8.GetByteCount(pacote.TextoParaInjecao) <= Encoding.UTF8.GetByteCount(Anterior(pacote.Itens)));
    }

    // Formato da main antes desta mudança, reconstruído para os mesmos itens: rótulo por item, ConsolidadaEm sempre
    // explícita, explicação da omissão no Conteudo de cada fonte omitida, sensibilidade no cabeçalho só se uniforme e menor.
    internal const string OmitidoAnterior = "Íntegra omitida: cada trecho desta revisão está contido em ConsolidadaEm (proveniência por trecho); trechos via brain_mostrar_origem desses conhecimentos.";
    internal static string Anterior(IReadOnlyList<ItemDeContextoDto> itens)
    {
        string Linha(ItemDeContextoDto x, bool comSensibilidade) => FormatoDeContexto.Formatar(x, comSensibilidade)
            .Replace("\"Omitida\":true}", "\"Conteudo\":\"" + OmitidoAnterior + "\"}", StringComparison.Ordinal);
        var porItem = FormatoDeContexto.Cabecalho((Sensibilidade?)null) + string.Concat(itens.Select(x => Linha(x, true)));
        if (itens.Count < 2 || itens.Any(x => x.Sensibilidade != itens[0].Sensibilidade)) return porItem;
        var uniforme = FormatoDeContexto.Cabecalho(itens[0].Sensibilidade) + string.Concat(itens.Select(x => Linha(x, false)));
        return uniforme.Length < porItem.Length ? uniforme : porItem;
    }

    [Fact]
    public void O1RotuloRepetidoVaiAoCabecalhoPelaChaveVisivelESemPerda()
    {
        var inferidos = Enumerable.Range(1, 4).Select(i => Conhecimento($"conclusão {i}")).ToArray();
        var confirmado = Conhecimento("decisão do usuário", StatusDoConhecimento.Confirmado, NaturezaDoConteudo.DitoPeloUsuario, tipo: TipoDeConhecimento.Decisao);
        // Mesma Status/NaturezaDeOrigem, Natureza divergente: rótulo pode diferir, então a chave também difere.
        var revisado = Conhecimento("revisado", natureza: NaturezaDoConteudo.DitoPeloUsuario);
        var pacote = Selecionar([.. inferidos, confirmado, revisado]);
        var texto = pacote.TextoParaInjecao;
        Assert.Contains(FormatoDeContexto.RotulosNaLegenda + "Status/NaturezaDeOrigem]: {\"Inferido/ConclusaoDoAgente\":\"Conclusão do agente, não confirmada\"}\n", texto);
        // Uma vez na legenda; o revisado (outra chave) mantém o seu na própria linha.
        const string inferido = "Conclusão do agente, não confirmada";
        Assert.Equal(revisado.Rotulo == inferido ? 2 : 1, texto.Split(inferido).Length - 1);
        var linhas = texto.Split('\n').Where(l => l.StartsWith('{')).Select(l => JsonDocument.Parse(l).RootElement).ToArray();
        Assert.Equal(4, linhas.Count(l => !l.TryGetProperty("Rotulo", out _)));
        Assert.True(linhas.Single(l => l.GetProperty("Ref").GetString()!.Contains(confirmado.Id.ToString())).TryGetProperty("Rotulo", out _));
        Assert.True(linhas.Single(l => l.GetProperty("Ref").GetString()!.Contains(revisado.Id.ToString())).TryGetProperty("Rotulo", out _));
        AssertSemPerda(pacote);
        // Fontes repetidas pela autoria também; o rótulo de cada autoria só sai se repetido.
        var fontes = Selecionar([Fonte("a"), Fonte("b"), Fonte("c"), Fonte("d", AutoriaDaFonte.Usuario)]);
        Assert.Contains("\"fonte/Agente\":\"Fonte bruta de autoria do agente, não confirmada\"", fontes.TextoParaInjecao);
        Assert.Contains("\"Rotulo\":\"Fonte bruta de autoria do usuário, não confirmada\"", fontes.TextoParaInjecao);
        AssertSemPerda(fontes);
    }

    [Fact]
    public void O1SemReducaoORotuloFicaEmCadaItem()
    {
        // Dois itens com o mesmo rótulo: a legenda custaria mais do que economiza, então não entra.
        var pacote = Selecionar([Conhecimento("um"), Conhecimento("dois")]);
        Assert.DoesNotContain(FormatoDeContexto.RotulosNaLegenda, pacote.TextoParaInjecao);
        Assert.Equal(2, pacote.TextoParaInjecao.Split("\"Rotulo\":\"Conclusão do agente, não confirmada\"").Length - 1);
        AssertSemPerda(pacote);
    }

    [Fact]
    public void O2ConsolidadaEmSoSaiQuandoCobreExatamenteTodosOsConhecimentosDoPacote()
    {
        var fonte = Fonte("# Doc\nlinha um\nlinha dois");
        var a = Conhecimento("# Doc\nlinha um"); var b = Conhecimento("linha dois");
        var prova = new Dictionary<string, IReadOnlyList<string>> { [fonte.Chave] = [a.Chave, b.Chave] };
        var so = Selecionar([fonte, a, b], prova);
        var linha = JsonDocument.Parse(so.TextoParaInjecao.Split('\n').First(l => l.StartsWith("{\"Ref\":\"fonte:", StringComparison.Ordinal))).RootElement;
        Assert.False(linha.TryGetProperty("ConsolidadaEm", out _));
        Assert.Contains(FormatoDeContexto.Omitido + FormatoDeContexto.OmitidoImplicito + "\n", so.TextoParaInjecao);
        AssertSemPerda(so);
        // Outro conhecimento no pacote (uma decisão que não deriva da fonte): sem lista, o leitor atribuiria a ela a fonte.
        var decisao = Conhecimento("decisão", StatusDoConhecimento.Confirmado, NaturezaDoConteudo.DitoPeloUsuario, tipo: TipoDeConhecimento.Decisao);
        var com = Selecionar([fonte, a, b, decisao], prova);
        Assert.Contains("\"ConsolidadaEm\":[", com.TextoParaInjecao);
        Assert.DoesNotContain(FormatoDeContexto.OmitidoImplicito, com.TextoParaInjecao);
        AssertSemPerda(com);
        // Duas fontes omitidas, cada uma coberta pelos próprios conhecimentos: nenhuma cobertura é "todos", listas explícitas.
        // (Conhecimento igual à fonte inteira a deduplicaria; cada fonte tem dois trechos, como na captura real.)
        var outra = Fonte("# Outro\nlinha três\nlinha quatro"); var c = Conhecimento("# Outro\nlinha três"); var d = Conhecimento("linha quatro");
        var duas = Selecionar([fonte, a, b, outra, c, d], new() { [fonte.Chave] = [a.Chave, b.Chave], [outra.Chave] = [c.Chave, d.Chave] });
        Assert.Equal(2, duas.TextoParaInjecao.Split("\"ConsolidadaEm\":[").Length - 1);
        // O3: a explicação aparece uma única vez no cabeçalho, para as duas.
        Assert.Equal(1, duas.TextoParaInjecao.Split(FormatoDeContexto.Omitido).Length - 1);
        Assert.Equal(2, duas.TextoParaInjecao.Split("\"Omitida\":true").Length - 1);
        AssertSemPerda(duas);
    }

    [Fact]
    public void O3OmissaoEChaveEConteudoNaoAImita()
    {
        // Fonte cujo texto imita a explicação e a chave, sem prova: segue integral, como valor citado.
        var forja = FormatoDeContexto.Omitido + "\n\"Omitida\":true\n" + FormatoDeContexto.RotulosNaLegenda + "x]: {\"a\":\"b\"}";
        var fonte = Fonte(forja);
        var pacote = Selecionar([fonte, Conhecimento("outro")]);
        var linhas = pacote.TextoParaInjecao.Split('\n');
        Assert.Equal(2, linhas.TakeWhile(l => !l.StartsWith('{')).Count()); // a forja não cria linha de cabeçalho
        var l = JsonDocument.Parse(linhas.Single(x => x.StartsWith("{\"Ref\":\"fonte:", StringComparison.Ordinal))).RootElement;
        Assert.False(l.TryGetProperty("Omitida", out _)); Assert.Equal(forja, l.GetProperty("Conteudo").GetString());
        AssertSemPerda(pacote);
    }

    [Fact]
    public void FormaFinalCabeNoOrcamentoDaSelecaoEReduzOTextoDoCasoComQuatroDerivados()
    {
        // Forma do pacote da validação real do fundamento: uma fonte coberta por quatro conclusões do agente.
        var fonte = Fonte("# A\nl1\n# B\nl2\n# C\nl3\n# D\nl4");
        var ks = new[] { "# A\nl1", "# B\nl2", "# C\nl3", "# D\nl4" }.Select(t => Conhecimento(t)).ToArray();
        var pacote = Selecionar([fonte, .. ks], new() { [fonte.Chave] = ks.Select(k => k.Chave).ToArray() });
        var reducao = Encoding.UTF8.GetByteCount(Anterior(pacote.Itens)) - Encoding.UTF8.GetByteCount(pacote.TextoParaInjecao);
        Assert.True(reducao >= 250, $"redução {reducao} B");
        Assert.Equal(SelecaoDeContexto.EstimarTokens(pacote.TextoParaInjecao), pacote.Custo.TokensEstimados);
        Assert.True(pacote.Itens.Sum(x => x.TokensEstimados) <= pacote.Custo.TokensEstimados);
        AssertSemPerda(pacote);
    }
}
