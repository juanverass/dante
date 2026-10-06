using Dante.Application.Planilhas;

namespace Dante.Tests;

// #224: coordenadas A1, busca e validação de entrada da capacidade genérica de planilhas, sem provedor.
public sealed class PlanilhasNucleoTests
{
    [Theory]
    [InlineData("B12", null, 12, 2, 12, 2, "B12")]
    [InlineData("Gastos!B2:D5", "Gastos", 2, 2, 5, 4, "'Gastos'!B2:D5")]
    [InlineData("'Aba com espaço'!$A$1", "Aba com espaço", 1, 1, 1, 1, "'Aba com espaço'!A1")]
    [InlineData("'D''Ávila'!c3:a1", "D'Ávila", 1, 1, 3, 3, "'D''Ávila'!A1:C3")]
    [InlineData("Plan1!AA10", "Plan1", 10, 27, 10, 27, "'Plan1'!AA10")]
    public void IntervaloA1InterpretaEFormataRetangulos(string texto, string? aba, int linha, int coluna, int linhaFinal,
        int colunaFinal, string formatado)
    {
        var intervalo = IntervaloA1.Interpretar(texto);
        Assert.Equal((aba, linha, coluna, linhaFinal, colunaFinal, false),
            (intervalo.Aba, intervalo.Linha, intervalo.Coluna, intervalo.LinhaFinal, intervalo.ColunaFinal, intervalo.AbaInteira));
        Assert.Equal(formatado, intervalo.ToString());
        Assert.Equal(intervalo, IntervaloA1.Interpretar(intervalo.ToString()));
    }

    [Fact]
    public void IntervaloA1AceitaAbaInteiraERecusaFormasAbertas()
    {
        var aba = IntervaloA1.Interpretar("Resumo anual");
        Assert.True(aba.AbaInteira);
        Assert.Equal("'Resumo anual'", aba.ToString());
        Assert.True(IntervaloA1.Interpretar("'Resumo!2026'").AbaInteira);
        foreach (var invalido in new[] { "", "Aba!", "Aba!A:C", "Aba!2:5", "'Aba", "Aba!A0", "Aba!AAAA1" })
            Assert.False(IntervaloA1.TentarInterpretar(invalido, out _), invalido);
        Assert.Equal("ZZ", IntervaloA1.NomeDaColuna(702));
        Assert.Equal(703, IntervaloA1.IndiceDaColuna("aaa"));
    }

    [Fact]
    public void BuscaIgnoraCaixaAcentosEEspacosEPoeExatasPrimeiro()
    {
        var fonte = new ValoresLidos(IntervaloA1.DaCelula("Plan1", 3, 2),
        [
            ["Conta de luz", "120"],
            ["  CONTA   DE ÁGUA ", "80"],
            ["conta de agua", "79"]
        ]);
        var (encontradas, truncado) = BuscaNaPlanilha.Buscar([fonte], "conta de agua", 10);
        Assert.False(truncado);
        Assert.Equal(["B4", "B5"], encontradas.Select(e => e.Ocorrencia.Celula.Endereco).Order());
        Assert.All(encontradas, e => Assert.True(e.Exata));
        var linha = encontradas.First(e => e.Ocorrencia.Celula.Endereco == "B5").Ocorrencia.Linha;
        Assert.Equal(["B5", "C5"], linha.Select(c => c.Endereco));
        Assert.Equal("79", linha[1].ValorExibido);

        var (parciais, _) = BuscaNaPlanilha.Buscar([fonte], "conta", 2);
        Assert.Equal(2, parciais.Count);
        Assert.Equal(3, BuscaNaPlanilha.Candidatos(BuscaNaPlanilha.Buscar([fonte], "conta", 10).Encontradas).Count);
        Assert.Single(BuscaNaPlanilha.Candidatos(BuscaNaPlanilha.Buscar([fonte], "luz", 10).Encontradas));
    }

    [Fact]
    public void ValidatorValidaAliasEFormaDasEscritas()
    {
        Assert.Equal("financas", PlanilhaValidator.ExigirAlias("@Financas"));
        Assert.Throws<ArgumentException>(() => PlanilhaValidator.ExigirAlias("1x"));

        EscritaNaPlanilhaDto Escrita(string intervalo, params ValorDeCelula[][] valores) => new()
        {
            Planilha = "financas", Alteracoes = [new AlteracaoDeIntervaloDto { Intervalo = intervalo, Valores = valores }]
        };
        Assert.Single(PlanilhaValidator.ExigirEscrita(Escrita("Gastos!B2:C2", [ValorDeCelula.DeNumero(1), ValorDeCelula.Vazio])));
        Assert.Contains("aba", Assert.Throws<ArgumentException>(() =>
            PlanilhaValidator.ExigirEscrita(Escrita("B2", [ValorDeCelula.Vazio]))).Message);
        Assert.Contains("1 linha(s) de 2 coluna(s)", Assert.Throws<ArgumentException>(() =>
            PlanilhaValidator.ExigirEscrita(Escrita("Gastos!B2:C2", [ValorDeCelula.Vazio]))).Message);
        Assert.Throws<ArgumentException>(() => PlanilhaValidator.ExigirEscrita(Escrita("Gastos", [ValorDeCelula.Vazio])));
        var grande = Enumerable.Range(0, 201).Select(_ => new[] { ValorDeCelula.Vazio }).ToArray();
        Assert.Contains("200", Assert.Throws<ArgumentException>(() =>
            PlanilhaValidator.ExigirEscrita(Escrita("Gastos!A1:A201", grande))).Message);
        Assert.Throws<ArgumentException>(() => PlanilhaValidator.ExigirReferencia(new EscritaPorReferenciaDto
        {
            Planilha = "financas", Referencia = "AWS", Valor = ValorDeCelula.DeNumero(1)
        }));
    }
}
