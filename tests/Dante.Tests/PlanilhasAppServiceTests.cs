using System.Text.Json;
using Dante.Application.Planilhas;

namespace Dante.Tests;

// #224: regras da fachada genérica de planilhas sobre os adapters reais e o GoogleSheetsFalso — cadastro, leitura
// progressiva, busca, escrita exata com valor anterior e auditoria, e recusa de escrita ambígua, divergente,
// sobre fórmula, dentro de mesclagem ou de limpeza em massa.
public sealed class PlanilhasAppServiceTests
{
    private static readonly OrigemDaSolicitacao Origem = new("mcp", "telegram:42", "codex");

    private sealed class AuditoriaQueFalha : IAuditoriaDePlanilhas
    {
        public Task RegistrarAsync(RegistroDeAuditoriaDePlanilha registro, CancellationToken cancellationToken = default) =>
            throw new IOException("Disco indisponível");
    }

    [Fact]
    public async Task FalhaDeAuditoriaInformaQueAppendJaFoiAplicado()
    {
        using var ambiente = Financas();
        await ambiente.CadastrarAsync();
        var servico = new PlanilhasAppService(ambiente.Adapter, ambiente.OAuth, ambiente.Cadastro, new AuditoriaQueFalha());
        var falha = await Assert.ThrowsAsync<FalhaDePlanilhaException>(() => servico.AdicionarLinhaAsync(
            new AdicaoDeLinhaDto { Planilha = "financas", Intervalo = "Gastos!A1:C1", Valores = [ValorDeCelula.DeTexto("Nova conta"), ValorDeCelula.DeNumero(10), ValorDeCelula.DeNumero(20)] }, Origem));
        Assert.Contains("A escrita foi aplicada", falha.Message);
        Assert.Contains("Não repita", falha.Message);
        Assert.Single(ambiente.Google.Escritas);
        Assert.Equal("Nova conta", ambiente.Google.Exibido("Gastos", "A5"));
    }

    private static AmbienteDePlanilhas Financas()
    {
        var ambiente = new AmbienteDePlanilhas();
        ambiente.Google.Titulo = "Finanças 2026";
        ambiente.Google.AdicionarAba("Gastos");
        ambiente.Google.AdicionarAba("Resumo anual");
        foreach (var (endereco, valor) in new (string, object)[]
                 {
                     ("A1", "Conta"), ("B1", "Setembro"), ("C1", "Outubro"),
                     ("A2", "Internet"), ("B2", 99.9), ("C2", 99.9),
                     ("A3", "AWS"), ("B3", 110.5), ("C3", 120),
                     ("A4", "Internet móvel"), ("B4", 49.9)
                 })
            ambiente.Google.Definir("Gastos", endereco, valor);
        ambiente.Google.Definir("Gastos", "B6", 260.3, formula: "=SUM(B2:B4)", exibido: "260,3");
        ambiente.Google.Definir("Resumo anual", "A1", "Total");
        ambiente.Google.Mesclar("Resumo anual", "A1:C1");
        return ambiente;
    }

    [Fact]
    public async Task CadastroConfirmaAcessoERecusaAliasOuPlanilhaDuplicados()
    {
        using var ambiente = Financas();
        var url = $"https://docs.google.com/spreadsheets/d/{ambiente.Google.IdDaPlanilha}/edit#gid=0";
        var cadastrada = await ambiente.Servico.CadastrarAsync("Financas", url, "gastos da casa");
        Assert.Equal(("financas", ambiente.Google.IdDaPlanilha, "Finanças 2026", "gastos da casa"),
            (cadastrada.Alias, cadastrada.IdDaPlanilha, cadastrada.Titulo, cadastrada.Descricao));
        Assert.Equal(MotivoDaFalhaDePlanilha.Conflito,
            await Motivo(() => ambiente.Servico.CadastrarAsync("outra", url)));
        Assert.Equal(MotivoDaFalhaDePlanilha.NaoEncontrada,
            await Motivo(() => ambiente.Servico.CadastrarAsync("x", "1ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ")));
        Assert.Single(await ambiente.Servico.ListarAsync());
        Assert.True(await ambiente.Servico.RemoverAsync("@financas"));
        Assert.Empty(await ambiente.Servico.ListarAsync());
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(ambiente.Cadastro.Caminho));
    }

    [Fact]
    public async Task NadaForaDoCadastroELidoOuEscrito()
    {
        using var ambiente = Financas();
        Assert.Equal(MotivoDaFalhaDePlanilha.NaoCadastrada, await Motivo(() => ambiente.Servico.LerAsync("financas", "Gastos!A1")));
        Assert.Equal(MotivoDaFalhaDePlanilha.NaoCadastrada, await Motivo(() => ambiente.Servico.LerAsync(ambiente.Google.IdDaPlanilha, "Gastos!A1")));
        Assert.DoesNotContain(ambiente.Google.Requisicoes, r => r.Url.Contains("sheets.googleapis.com"));
    }

    [Fact]
    public async Task DescricaoTrazAbasAreaUsadaMesclagensERegioesAnotadas()
    {
        using var ambiente = Financas();
        await ambiente.CadastrarAsync();
        await ambiente.Servico.AnotarRegiaoAsync("financas", "mensal", "Gastos!A1:C4", "uma linha por conta, um mês por coluna");
        var descricao = await ambiente.Servico.DescreverAsync("financas");
        Assert.Equal(["Gastos", "Resumo anual"], descricao.Planilha.Abas.Select(a => a.Titulo));
        Assert.Equal("A1:C6", descricao.Planilha.Abas[0].AreaUsada);
        Assert.Equal(["A1:C1"], descricao.Planilha.Abas[1].Mesclagens);
        Assert.Equal("pt_BR", descricao.Planilha.Localidade);
        var regiao = Assert.Single(descricao.Cadastro.Regioes);
        Assert.Equal(("mensal", "'Gastos'!A1:C4"), (regiao.Nome, regiao.Intervalo));

        var leitura = await ambiente.Servico.LerAsync("financas", "MENSAL");
        Assert.Equal("'Gastos'!A1:C4", leitura.Intervalo);
        Assert.Equal(11, leitura.Celulas.Count);
    }

    [Fact]
    public async Task LeituraTrazCoordenadasExibidoBrutoFormulaEMesclagemELimiteTrunca()
    {
        using var ambiente = Financas();
        await ambiente.CadastrarAsync();
        var leitura = await ambiente.Servico.LerAsync("financas", "Gastos!A2:C6");
        var b2 = leitura.Celulas.Single(c => c.Endereco == "B2");
        Assert.Equal(("99,9", "99.9", 2, 2), (b2.ValorExibido, b2.ValorBruto!.ToString(), b2.Linha, b2.Coluna));
        Assert.Equal("=SUM(B2:B4)", leitura.Celulas.Single(c => c.Endereco == "B6").Formula);
        Assert.DoesNotContain(leitura.Celulas, c => c.Linha == 5);

        var resumo = await ambiente.Servico.LerAsync("financas", "'Resumo anual'!A1:C2");
        Assert.Equal("A1:C1", resumo.Celulas.Single(c => c.Endereco == "A1").Mesclagem);
        Assert.Equal(["A1:C1"], resumo.Mesclagens);

        var truncada = await ambiente.Servico.LerAsync("financas", "Gastos", 6);
        Assert.True(truncada.Truncado);
        Assert.Equal("'Gastos'!A1:C2", truncada.Intervalo);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ambiente.Servico.LerAsync("financas", "Gastos!A1", 5001));
        await Assert.ThrowsAsync<ArgumentException>(() => ambiente.Servico.LerAsync("financas", "A1"));
    }

    [Fact]
    public async Task BuscaPercorreTodasAsAbasERetornaLinhaDeContexto()
    {
        using var ambiente = Financas();
        await ambiente.CadastrarAsync();
        var busca = await ambiente.Servico.BuscarAsync("financas", "internet");
        Assert.Equal(["Gastos", "Resumo anual"], busca.AbasConsultadas);
        Assert.Equal(["A2", "A4"], busca.Ocorrencias.Select(o => o.Celula.Endereco));
        Assert.Equal(["A2", "B2", "C2"], busca.Ocorrencias[0].Linha.Select(c => c.Endereco));
        Assert.Single((await ambiente.Servico.BuscarAsync("financas", "total", aba: "Resumo anual")).Ocorrencias);
        Assert.Empty((await ambiente.Servico.BuscarAsync("financas", "internet", intervalo: "A3:C3", aba: "Gastos")).Ocorrencias);
    }

    [Fact]
    public async Task EscritaExataGuardaValorAnteriorEAudita()
    {
        using var ambiente = Financas();
        await ambiente.CadastrarAsync();
        var resultado = await ambiente.Servico.AtualizarAsync(new EscritaNaPlanilhaDto
        {
            Planilha = "financas",
            Alteracoes =
            [
                new AlteracaoDeIntervaloDto
                {
                    Intervalo = "Gastos!C2:C3",
                    Valores = [[ValorDeCelula.DeTexto("119,90")], [ValorDeCelula.DeNumero(129.9)]],
                    ValoresEsperados = [["99,9"], ["120"]]
                }
            ]
        }, Origem);
        Assert.Equal(["Gastos!C2:C3"], resultado.Intervalos);
        Assert.Equal([("C2", "99,9", "119,90"), ("C3", "120", "129.9")],
            resultado.Celulas.Select(c => (c.Endereco, c.ValorAnterior, c.ValorNovo)));
        Assert.Equal(119.9, ambiente.Google.Obter("Gastos", "C2")!.Numero);
        Assert.Equal("129,9", ambiente.Google.Exibido("Gastos", "C3"));
        Assert.Equal("99,9", ambiente.Google.Exibido("Gastos", "B2"));

        var auditoria = ambiente.LinhasDeAuditoria().Select(l => JsonDocument.Parse(l).RootElement).ToArray();
        Assert.Equal(2, auditoria.Length);
        Assert.Equal(("pessoa@example.com", "financas", "Gastos", "atualizar", "C2", "99,9", "119,90", "mcp:telegram:42", "codex"),
            (auditoria[0].GetProperty("Conta").GetString(), auditoria[0].GetProperty("Alias").GetString(),
             auditoria[0].GetProperty("Aba").GetString(), auditoria[0].GetProperty("Operacao").GetString(),
             auditoria[0].GetProperty("Endereco").GetString(), auditoria[0].GetProperty("ValorAnterior").GetString(),
             auditoria[0].GetProperty("ValorNovo").GetString(), auditoria[0].GetProperty("Origem").GetString(),
             auditoria[0].GetProperty("Agente").GetString()));
        Assert.DoesNotContain(GoogleSheetsFalso.RefreshToken, File.ReadAllText(ambiente.Auditoria.Caminho));
        Assert.DoesNotContain("at-", File.ReadAllText(ambiente.Auditoria.Caminho));
    }

    [Fact]
    public async Task ValorEsperadoDivergenteRecusaOLoteInteiro()
    {
        using var ambiente = Financas();
        await ambiente.CadastrarAsync();
        var falha = await Assert.ThrowsAsync<FalhaDePlanilhaException>(() => ambiente.Servico.AtualizarAsync(new EscritaNaPlanilhaDto
        {
            Planilha = "financas",
            Alteracoes =
            [
                new AlteracaoDeIntervaloDto { Intervalo = "Gastos!C2", Valores = [[ValorDeCelula.DeNumero(1)]] },
                new AlteracaoDeIntervaloDto { Intervalo = "Gastos!C3", Valores = [[ValorDeCelula.DeNumero(2)]], ValoresEsperados = [["999"]] }
            ]
        }, Origem));
        Assert.Equal(MotivoDaFalhaDePlanilha.Conflito, falha.Motivo);
        Assert.Contains("C3: esperado \"999\", atual \"120\"", falha.Message);
        Assert.Empty(ambiente.Google.Escritas);
        Assert.Empty(ambiente.LinhasDeAuditoria());
    }

    [Fact]
    public async Task FormulaSoESobrescritaComPermissaoExplicita()
    {
        using var ambiente = Financas();
        await ambiente.CadastrarAsync();
        var escrita = new EscritaNaPlanilhaDto
        {
            Planilha = "financas",
            Alteracoes = [new AlteracaoDeIntervaloDto { Intervalo = "Gastos!B6", Valores = [[ValorDeCelula.DeNumero(0)]] }]
        };
        var falha = await Assert.ThrowsAsync<FalhaDePlanilhaException>(() => ambiente.Servico.AtualizarAsync(escrita, Origem));
        Assert.Contains("B6 (=SUM(B2:B4))", falha.Message);
        Assert.Empty(ambiente.Google.Escritas);
        var resultado = await ambiente.Servico.AtualizarAsync(escrita with { PermitirSobrescreverFormulas = true }, Origem);
        Assert.Equal("=SUM(B2:B4)", Assert.Single(resultado.Celulas).FormulaAnterior);
    }

    [Fact]
    public async Task CelulaInternaDeMesclagemELimpezaEmMassaSaoRecusadas()
    {
        using var ambiente = Financas();
        await ambiente.CadastrarAsync();
        var mesclada = await Assert.ThrowsAsync<FalhaDePlanilhaException>(() => ambiente.Servico.AtualizarAsync(new EscritaNaPlanilhaDto
        {
            Planilha = "financas",
            Alteracoes = [new AlteracaoDeIntervaloDto { Intervalo = "'Resumo anual'!B1", Valores = [[ValorDeCelula.DeTexto("x")]] }]
        }, Origem));
        Assert.Contains("mesclagem A1:C1", mesclada.Message);

        for (var linha = 10; linha < 31; linha++) ambiente.Google.Definir("Gastos", $"A{linha}", "x");
        var limpeza = await Assert.ThrowsAsync<FalhaDePlanilhaException>(() => ambiente.Servico.AtualizarAsync(new EscritaNaPlanilhaDto
        {
            Planilha = "financas",
            Alteracoes = [new AlteracaoDeIntervaloDto { Intervalo = "Gastos!A10:A30", Valores = Enumerable.Range(0, 21).Select(_ => (IReadOnlyList<ValorDeCelula>)[ValorDeCelula.Vazio]).ToArray() }]
        }, Origem));
        Assert.Equal(MotivoDaFalhaDePlanilha.NaoSuportada, limpeza.Motivo);
        Assert.Empty(ambiente.Google.Escritas);
    }

    [Fact]
    public async Task CorrespondenciaExataVenceParcialNaEscritaPorReferencia()
    {
        using var ambiente = Financas();
        await ambiente.CadastrarAsync();
        // "Internet" é exata e vence a parcial "Internet móvel": ambiguidade é empate entre candidatos do mesmo nível.
        var resultado = await ambiente.Servico.AtualizarPorReferenciaAsync(
            new EscritaPorReferenciaDto { Planilha = "financas", Referencia = "internet", Coluna = "C", Valor = ValorDeCelula.DeTexto("119,90") }, Origem);
        Assert.Equal("C2", Assert.Single(resultado.Celulas).Endereco);
        Assert.Equal(MotivoDaFalhaDePlanilha.NaoEncontrada, await Motivo(() => ambiente.Servico.AtualizarPorReferenciaAsync(
            new EscritaPorReferenciaDto { Planilha = "financas", Referencia = "energia", Coluna = "C", Valor = ValorDeCelula.Vazio }, Origem)));
    }

    [Fact]
    public async Task EscritaPorReferenciaUnicaEscreveNaLinhaDaReferencia()
    {
        using var ambiente = Financas();
        ambiente.Google.Definir("Gastos", "A8", "aws");
        await ambiente.CadastrarAsync();
        var ambigua = await Assert.ThrowsAsync<FalhaDePlanilhaException>(() => ambiente.Servico.AtualizarPorReferenciaAsync(
            new EscritaPorReferenciaDto { Planilha = "financas", Referencia = "AWS", Coluna = "C", Valor = ValorDeCelula.DeTexto("129,90") }, Origem));
        Assert.Equal(["A3", "A8"], ambigua.Candidatos.Select(c => c.Celula.Endereco));
        Assert.Empty(ambiente.Google.Escritas);

        var resultado = await ambiente.Servico.AtualizarPorReferenciaAsync(new EscritaPorReferenciaDto
        {
            Planilha = "financas", Referencia = "AWS", Intervalo = "Gastos!A1:C4", DeslocamentoDeColunas = 2,
            Valor = ValorDeCelula.DeTexto("129,90"), ValorEsperado = "120"
        }, Origem);
        var celula = Assert.Single(resultado.Celulas);
        Assert.Equal(("C3", "120", "129,90"), (celula.Endereco, celula.ValorAnterior, celula.ValorNovo));
        Assert.Equal("129,90", ambiente.Google.Exibido("Gastos", "C3"));
        Assert.Contains("\"Operacao\":\"atualizar_por_referencia\"", Assert.Single(ambiente.LinhasDeAuditoria()));
    }

    [Fact]
    public async Task AdicionarLinhaEscreveAposATabelaSemTocarOResto()
    {
        using var ambiente = Financas();
        await ambiente.CadastrarAsync();
        var resultado = await ambiente.Servico.AdicionarLinhaAsync(new AdicaoDeLinhaDto
        {
            Planilha = "financas", Intervalo = "Gastos!A1:C1",
            Valores = [ValorDeCelula.DeTexto("Streaming"), ValorDeCelula.DeNumero(39.9), ValorDeCelula.Vazio]
        }, Origem);
        Assert.Equal(["'Gastos'!A5:C5"], resultado.Intervalos);
        Assert.Equal(["A5", "B5", "C5"], resultado.Celulas.Select(c => c.Endereco));
        Assert.Equal("Streaming", ambiente.Google.Exibido("Gastos", "A5"));
        Assert.Equal("260,3", ambiente.Google.Exibido("Gastos", "B6"));
        Assert.Equal(3, ambiente.LinhasDeAuditoria().Length);
    }

    private static async Task<MotivoDaFalhaDePlanilha> Motivo(Func<Task> acao) =>
        (await Assert.ThrowsAsync<FalhaDePlanilhaException>(acao)).Motivo;
}
