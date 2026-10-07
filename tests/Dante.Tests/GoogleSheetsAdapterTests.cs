using System.Net;
using System.Text.Json.Nodes;
using Dante.Application.Planilhas;

namespace Dante.Tests;

// #224: tradução da porta genérica para o Google Sheets API — renovação após 401, repetição de 429/5xx, erros com
// mensagem segura e o payload exato das escritas (USER_ENTERED, números como números, limpeza por string vazia).
public sealed class GoogleSheetsAdapterTests
{
    private static AmbienteDePlanilhas ComAba()
    {
        var ambiente = new AmbienteDePlanilhas();
        ambiente.Google.AdicionarAba("Dados");
        ambiente.Google.Definir("Dados", "A1", "x");
        return ambiente;
    }

    [Theory]
    [InlineData("https://docs.google.com/spreadsheets/d/" + GoogleSheetsFalso.IdPadrao + "/edit#gid=0", GoogleSheetsFalso.IdPadrao)]
    [InlineData("https://docs.google.com/spreadsheets/u/1/d/" + GoogleSheetsFalso.IdPadrao + "/view", GoogleSheetsFalso.IdPadrao)]
    [InlineData("  " + GoogleSheetsFalso.IdPadrao + " ", GoogleSheetsFalso.IdPadrao)]
    [InlineData("https://example.com/spreadsheets/d/" + GoogleSheetsFalso.IdPadrao, null)]
    [InlineData("financas", null)]
    public void IdentificaPlanilhaPelaUrlOuPeloId(string texto, string? id)
    {
        using var ambiente = new AmbienteDePlanilhas();
        Assert.Equal(id, ambiente.Adapter.IdentificarPlanilha(texto));
    }

    [Fact]
    public async Task AppendRecusadoPor401Ou429PodeSerRepetido()
    {
        using var ambiente = ComAba();
        ambiente.Google.TokenRecusado = "at-1";
        ambiente.Google.FalhasDaApi.Enqueue(HttpStatusCode.TooManyRequests);
        await ambiente.Adapter.AdicionarLinhaAsync(ambiente.Google.IdDaPlanilha,
            IntervaloA1.Interpretar("Dados!A1"), [ValorDeCelula.DeTexto("nova")]);
        Assert.Equal(3, ambiente.Google.Escritas.Count());
        Assert.Equal("nova", ambiente.Google.Exibido("Dados", "A2"));
        Assert.Null(ambiente.Google.Exibido("Dados", "A3"));
    }

    [Fact]
    public async Task RespostaGrandeMesmoEmIntervaloPequenoERecusada()
    {
        using var ambiente = ComAba();
        ambiente.Google.Definir("Dados", "A1", new string('x', 17 * 1024 * 1024));
        var falha = await Assert.ThrowsAsync<FalhaDePlanilhaException>(() => ambiente.Adapter.LerValoresExibidosAsync(
            ambiente.Google.IdDaPlanilha, [IntervaloA1.Interpretar("Dados!A1")]));
        Assert.Equal(MotivoDaFalhaDePlanilha.NaoSuportada, falha.Motivo);
        Assert.Contains("16 MB", falha.Message);
    }

    [Fact]
    public async Task AppendAplicadoComRespostaPerdidaNaoERepetido()
    {
        using var ambiente = ComAba();
        ambiente.Google.FalharDepoisDoAppend = true;
        var falha = await Assert.ThrowsAsync<FalhaDePlanilhaException>(() =>
            ambiente.Adapter.AdicionarLinhaAsync(ambiente.Google.IdDaPlanilha,
                IntervaloA1.Interpretar("Dados!A1"), [ValorDeCelula.DeTexto("nova") ]));
        Assert.Contains("não repita automaticamente", falha.Message);
        Assert.Single(ambiente.Google.Escritas);
        Assert.Equal("nova", ambiente.Google.Exibido("Dados", "A2"));
        Assert.Null(ambiente.Google.Exibido("Dados", "A3"));
    }

    [Fact]
    public async Task AppendComErroDoServidorNaoERepetido()
    {
        using var ambiente = ComAba();
        ambiente.Google.FalhasDaApi.Enqueue(HttpStatusCode.ServiceUnavailable);
        var falha = await Assert.ThrowsAsync<FalhaDePlanilhaException>(() =>
            ambiente.Adapter.AdicionarLinhaAsync(ambiente.Google.IdDaPlanilha,
                IntervaloA1.Interpretar("Dados!A1"), [ValorDeCelula.DeTexto("nova")]));
        Assert.Contains("Confira a planilha", falha.Message);
        Assert.Single(ambiente.Google.Escritas);
    }

    [Fact]
    public async Task GradeGrandeNaoECarregadaParaDescricaoOuBusca()
    {
        using var ambiente = new AmbienteDePlanilhas();
        ambiente.Google.AdicionarAba("Grande", 1000000, 100);
        var metadados = await ambiente.Adapter.ObterMetadadosAsync(ambiente.Google.IdDaPlanilha, true);
        Assert.Null(Assert.Single(metadados.Abas).AreaUsada);
        await Assert.ThrowsAsync<FalhaDePlanilhaException>(() => ambiente.Adapter.LerValoresExibidosAsync(
            ambiente.Google.IdDaPlanilha, [IntervaloA1.DaAba("Grande")]));
        Assert.DoesNotContain(ambiente.Google.Requisicoes, r => r.Url.Contains("values:batchGet"));
        var leitura = await ambiente.Adapter.LerAsync(ambiente.Google.IdDaPlanilha, IntervaloA1.Interpretar("Grande!A1:B3"), 6);
        Assert.Empty(leitura.Celulas);
    }

    [Fact]
    public async Task TokenRecusadoRenovaUmaVezERepete()
    {
        using var ambiente = ComAba();
        ambiente.Google.TokenRecusado = "at-1";
        var metadados = await ambiente.Adapter.ObterMetadadosAsync(ambiente.Google.IdDaPlanilha, false);
        Assert.Equal("Dados", Assert.Single(metadados.Abas).Titulo);
        Assert.Equal(["at-1", "at-2"], ambiente.Google.Requisicoes.Where(r => r.Url.Contains("sheets.googleapis.com")).Select(r => r.Token));

        ambiente.Google.TokenRecusado = "at-2";
        ambiente.Google.RefreshInvalido = true;
        Assert.Equal(MotivoDaFalhaDePlanilha.NaoConectada, (await Assert.ThrowsAsync<FalhaDePlanilhaException>(() =>
            ambiente.Adapter.ObterMetadadosAsync(ambiente.Google.IdDaPlanilha, false))).Motivo);
    }

    [Fact]
    public async Task LimiteDeTaxaEFalhaTemporariaSaoRepetidosComEspera()
    {
        using var ambiente = ComAba();
        ambiente.Google.FalhasDaApi.Enqueue(HttpStatusCode.TooManyRequests);
        ambiente.Google.FalhasDaApi.Enqueue(HttpStatusCode.ServiceUnavailable);
        await ambiente.Adapter.ObterMetadadosAsync(ambiente.Google.IdDaPlanilha, false);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], ambiente.Esperas);

        for (var i = 0; i < 4; i++) ambiente.Google.FalhasDaApi.Enqueue(HttpStatusCode.TooManyRequests);
        Assert.Equal(MotivoDaFalhaDePlanilha.LimiteDeTaxa, (await Assert.ThrowsAsync<FalhaDePlanilhaException>(() =>
            ambiente.Adapter.ObterMetadadosAsync(ambiente.Google.IdDaPlanilha, false))).Motivo);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, MotivoDaFalhaDePlanilha.SemPermissao)]
    [InlineData(HttpStatusCode.NotFound, MotivoDaFalhaDePlanilha.NaoEncontrada)]
    [InlineData(HttpStatusCode.BadRequest, MotivoDaFalhaDePlanilha.Invalida)]
    [InlineData(HttpStatusCode.Conflict, MotivoDaFalhaDePlanilha.Indisponivel)]
    public async Task ErrosDaApiViramFalhasSemToken(HttpStatusCode status, MotivoDaFalhaDePlanilha motivo)
    {
        using var ambiente = ComAba();
        ambiente.Google.FalhasDaApi.Enqueue(status);
        var falha = await Assert.ThrowsAsync<FalhaDePlanilhaException>(() =>
            ambiente.Adapter.LerAsync(ambiente.Google.IdDaPlanilha, IntervaloA1.Interpretar("Dados!A1"), 10));
        Assert.Equal(motivo, falha.Motivo);
        Assert.DoesNotContain("at-", falha.Message);
        Assert.DoesNotContain("Bearer", falha.Message);
        Assert.DoesNotContain("googleapis.com", falha.Message);
    }

    [Fact]
    public async Task IntervaloInexistenteDevolveMensagemDoGoogle()
    {
        using var ambiente = ComAba();
        var falha = await Assert.ThrowsAsync<FalhaDePlanilhaException>(() =>
            ambiente.Adapter.LerAsync(ambiente.Google.IdDaPlanilha, IntervaloA1.Interpretar("Inexistente!A1"), 10));
        Assert.Equal(MotivoDaFalhaDePlanilha.Invalida, falha.Motivo);
        Assert.Contains("Unable to parse range", falha.Message);
    }

    [Fact]
    public async Task EscritaUsaUserEnteredComTiposJsonEStringVaziaParaLimpar()
    {
        using var ambiente = ComAba();
        var atualizados = await ambiente.Adapter.AtualizarAsync(ambiente.Google.IdDaPlanilha,
        [
            new ValoresParaEscrita(IntervaloA1.Interpretar("Dados!A1:D1"),
                [[ValorDeCelula.Vazio, ValorDeCelula.DeNumero(32.5), ValorDeCelula.DeBooleano(true), ValorDeCelula.DeTexto("=B1*2")]])
        ]);
        Assert.Equal(["Dados!A1:D1"], atualizados);
        var corpo = JsonNode.Parse(ambiente.Google.Escritas.Single().Corpo!)!;
        Assert.Equal("USER_ENTERED", (string?)corpo["valueInputOption"]);
        Assert.Equal("""["",32.5,true,"=B1*2"]""", corpo["data"]![0]!["values"]![0]!.ToJsonString());
        Assert.Equal("'Dados'!A1:D1", (string?)corpo["data"]![0]!["range"]);
        Assert.Null(ambiente.Google.Obter("Dados", "A1"));
        Assert.Equal("=B1*2", ambiente.Google.Obter("Dados", "D1")!.Formula);
    }

    [Fact]
    public async Task AbaInteiraEReduzidaAAreaComDadosAntesDaGrade()
    {
        using var ambiente = ComAba();
        ambiente.Google.Definir("Dados", "C40", 7);
        var leitura = await ambiente.Adapter.LerAsync(ambiente.Google.IdDaPlanilha, IntervaloA1.DaAba("Dados"), 5_000);
        Assert.Equal("'Dados'!A1:C40", leitura.Intervalo);
        Assert.Equal(["A1", "C40"], leitura.Celulas.Select(c => c.Endereco));
        var grade = ambiente.Google.Requisicoes.Single(r => r.Url.Contains("includeGridData=true"));
        Assert.Contains(Uri.EscapeDataString("'Dados'!A1:C40"), grade.Url);
    }
}
