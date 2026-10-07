using Dante.Infrastructure.Google;
using Dante.Worker.Planilhas;
using Dante.Worker.Telegram;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dante.Tests;

// #224: /google e /planilha administram conta e cadastro; as sessões recebem o servidor MCP só com conta conectada,
// sem segredo nos argumentos.
public sealed class TelegramPlanilhasTests
{
    private static TelegramPlanilhas Comandos(AmbienteDePlanilhas ambiente) =>
        new(ambiente.Provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<TelegramPlanilhas>.Instance);

    [Fact]
    public async Task CadastroPeloTelegramConfirmaAcessoEDescreveAsAbas()
    {
        using var ambiente = new AmbienteDePlanilhas();
        ambiente.Google.AdicionarAba("Gastos").Mesclagens.Add(Dante.Application.Planilhas.IntervaloA1.Interpretar("A1:B1"));
        ambiente.Google.Definir("Gastos", "B3", 10);
        var comandos = Comandos(ambiente);
        Assert.Equal(TelegramPlanilhas.SintaxePlanilha, await comandos.PlanilhaAsync("", CancellationToken.None));
        Assert.Equal(TelegramPlanilhas.SintaxePlanilha, await comandos.PlanilhaAsync("add financas", CancellationToken.None));
        var cadastro = await comandos.PlanilhaAsync(
            $"add Financas https://docs.google.com/spreadsheets/d/{ambiente.Google.IdDaPlanilha}/edit gastos da casa", CancellationToken.None);
        Assert.StartsWith("Planilha cadastrada: financas — Planilha de teste", cadastro);
        Assert.Contains("gastos da casa", cadastro);
        Assert.Contains("- Gastos (A1:B3), 1 mesclagem(ns)", await comandos.PlanilhaAsync("show financas", CancellationToken.None));
        var lista = await comandos.ListarAsync(CancellationToken.None);
        Assert.Contains("Google Sheets: conectado (pessoa@example.com).", lista);
        Assert.Contains("- financas — Planilha de teste", lista);
        Assert.Contains("não foi alterada", await comandos.PlanilhaAsync("remove financas", CancellationToken.None));
        Assert.Contains("não está cadastrada", await comandos.PlanilhaAsync("show financas", CancellationToken.None));
        Assert.Equal("Alias inválido. Use de 1 a 40 letras minúsculas, números, _ ou -, começando com letra.",
            await comandos.PlanilhaAsync($"add 9x {ambiente.Google.IdDaPlanilha}", CancellationToken.None));
    }

    [Fact]
    public async Task GoogleConnectEnviaLinkEAvisaODesfechoSemSegredos()
    {
        using var ambiente = new AmbienteDePlanilhas(conectado: false);
        var comandos = Comandos(ambiente);
        Assert.Equal("Google Sheets: nenhuma conta conectada.", await comandos.GoogleAsync("status", _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal(TelegramPlanilhas.SintaxeGoogle, await comandos.GoogleAsync("talvez", _ => Task.CompletedTask, CancellationToken.None));

        var desfecho = new TaskCompletionSource<string>();
        var resposta = await comandos.GoogleAsync("connect", texto => { desfecho.SetResult(texto); return Task.CompletedTask; },
            CancellationToken.None);
        // O Telegram termina o link no primeiro espaço: a linha precisa chegar inteira e já codificada, com todos os
        // parâmetros que o Google exige.
        var linha = resposta.Split('\n')[^1];
        Assert.DoesNotContain(' ', linha);
        var link = new Uri(linha);
        Assert.Equal(linha, link.AbsoluteUri);
        Assert.Equal("accounts.google.com", link.Host);
        Assert.DoesNotContain("segredo-do-cliente", resposta);
        var consulta = link.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        Assert.Equal(GoogleOptions.Escopos, consulta["scope"]);
        Assert.Equal("S256", consulta["code_challenge_method"]);
        Assert.NotEmpty(consulta["code_challenge"]);
        using var navegador = new HttpClient();
        await navegador.GetAsync($"{consulta["redirect_uri"]}?state={Uri.EscapeDataString(consulta["state"])}&code=codigo-valido");
        var aviso = await desfecho.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Conta Google conectada: pessoa@example.com. Novas sessões passam a ter as ferramentas de planilha.", aviso);
        Assert.DoesNotContain(GoogleSheetsFalso.RefreshToken, aviso);

        Assert.StartsWith("Conta Google desconectada", await comandos.GoogleAsync("disconnect", _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal("Nenhuma conta Google estava conectada.", await comandos.GoogleAsync("disconnect", _ => Task.CompletedTask, CancellationToken.None));
    }

    [Fact]
    public async Task SemClienteConfiguradoOrientaAConfiguracao()
    {
        using var ambiente = new AmbienteDePlanilhas(conectado: false,
            opcoes: new GoogleOptions { DiretorioDaCredencial = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) });
        var comandos = Comandos(ambiente);
        Assert.Contains("Google__ClientId", await comandos.GoogleAsync("connect", _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal("Google Sheets: integração não configurada no D.A.N.T.E.",
            await comandos.GoogleAsync("", _ => Task.CompletedTask, CancellationToken.None));
    }

    [Fact]
    public async Task SessaoRecebeOServidorMcpSoComContaConectadaESemSegredos()
    {
        var configuracao = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DANTE_PLANILHAS_DIR"] = "relativo/planilhas"
        }).Build();
        using (var desconectado = new AmbienteDePlanilhas(conectado: false))
        {
            var ferramentas = new FerramentasDePlanilhaParaAgentes(desconectado.Provider.GetRequiredService<IServiceScopeFactory>(),
                configuracao, NullLogger<FerramentasDePlanilhaParaAgentes>.Instance, ("/opt/dante/Dante.Worker", []));
            Assert.Empty(await ferramentas.ForAsync(42, AgentKind.Codex));
        }

        using var ambiente = new AmbienteDePlanilhas();
        var oferecidas = new FerramentasDePlanilhaParaAgentes(ambiente.Provider.GetRequiredService<IServiceScopeFactory>(),
            configuracao, NullLogger<FerramentasDePlanilhaParaAgentes>.Instance, ("/usr/bin/dotnet", ["/opt/dante/Dante.Worker.dll"]));
        var servidor = Assert.Single(await oferecidas.ForAsync(42, AgentKind.Codex));
        Assert.Equal(("dante_planilhas", "/usr/bin/dotnet"), (servidor.Name, servidor.Command));
        Assert.Equal(["/opt/dante/Dante.Worker.dll", "--mcp-planilhas", "--origem", "telegram:42", "--agente", "codex"], servidor.Arguments);
        Assert.Equal(ServidorMcpDePlanilhas.FerramentasDeLeitura, servidor.ReadOnlyTools);
        Assert.Equal(Path.GetFullPath("relativo/planilhas"), servidor.Environment["DANTE_PLANILHAS_DIR"]);
        Assert.True(servidor.Environment.ContainsKey("HOME"));
        var linhaDeComando = string.Join(' ', servidor.Arguments.Concat(servidor.Environment.Values));
        Assert.DoesNotContain(GoogleSheetsFalso.RefreshToken, linhaDeComando);
        Assert.DoesNotContain("segredo-do-cliente", linhaDeComando);
    }
}
