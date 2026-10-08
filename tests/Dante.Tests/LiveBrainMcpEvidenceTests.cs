using System.Text;
using Dante.Infrastructure.Agentes;
using Dante.Worker.Brain;
using Dante.Worker.Sessions;
using Dante.Worker.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Dante.Tests;

// CLI real + MCP stdio/IPC + Application/PostgreSQL isolado. Não usa banco/conversa do usuário.
public sealed class LiveBrainMcpEvidenceTests(ITestOutputHelper output)
{
    [LiveBrainTheory]
    [InlineData(AgentKind.Codex)]
    [InlineData(AgentKind.Claude)]
    public async Task AgenteRealCapturaItensSeparadosENovaSessaoRecuperaProveniencia(AgentKind agente)
    {
        await using var banco = await Banco.CriarAsync(); using var app = BrainMcpTests.CriarApp(banco.ConnectionString);
        var brain = app.GetRequiredService<TelegramBrain>(); await BrainMcpTests.SelecionarAsync(brain);
        using var ferramentas = BrainMcpTests.Provedor(app); using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var root = Directory.CreateTempSubdirectory("dante-live-brain-").FullName;
        try
        {
            var pedido = "Crie conhecimentos no Brain para cada um dos três itens abaixo, separadamente. Preserve conteúdo e título exatos, tags dante. Não confirme ainda.\n" +
                "Título: Nome D.A.N.T.E.\nConteúdo: D.A.N.T.E. significa Distributed Agent Network for Task Execution.\n\n" +
                "Título: Orquestração local\nConteúdo: O D.A.N.T.E. coordena agentes de IA e ferramentas locais.\n\n" +
                "Título: Adapter Telegram\nConteúdo: Telegram é um adapter de interação do D.A.N.T.E.\n";
            var mensagem = BrainMcpTests.Mensagem(pedido); app.GetRequiredService<RegistroDeOperacoesBrain>().Observar(mensagem);
            var servidores = await ferramentas.ForSessionAsync("LIVE1", new(42, agente, Dante.Application.Contextos.JobExecutionContext.General(root), BrainConversation: mensagem.ParaFerramentas()), timeout.Token);
            Assert.Single(servidores);
            await using (var driver = Driver(agente))
            {
                await driver.StartAsync(new(root, IsGeneral: true, Profile: AgentPermissionProfile.Auto, ToolServers: servidores), timeout.Token);
                await using var eventos = driver.ReadEventsAsync(timeout.Token).GetAsyncEnumerator();
                await driver.StartTurnAsync(pedido, timeout.Token);
                var usadas = new List<string>();
                output.WriteLine(await RespostaAsync(driver, eventos, timeout.Token, usadas));
                Assert.Contains(usadas, x => x.Contains("brain_capturar_conhecimento", StringComparison.Ordinal));
                Assert.DoesNotContain(usadas, x => x.Contains("plugin", StringComparison.OrdinalIgnoreCase));
                var duplicado = BrainMcpTests.Mensagem("Crie no Brain este mesmo conteúdo e reporte possível duplicidade, sem confirmar: D.A.N.T.E. significa Distributed Agent Network for Task Execution.", 130);
                ferramentas.BeginTurn("LIVE1", new AgentInput(duplicado.Text!, []) { BrainConversation = duplicado.ParaFerramentas() });
                await driver.StartTurnAsync(duplicado.Text!, timeout.Token); output.WriteLine(await RespostaAsync(driver, eventos, timeout.Token));
            }
            ferramentas.EndSession("LIVE1");
            await using (var db = banco.Contexto())
            {
                var candidatos = await db.CandidatosDeConhecimento.ToListAsync(); Assert.Equal(3, candidatos.Count); Assert.Empty(await db.Conhecimentos.ToListAsync());
                Assert.All(candidatos, c => { Assert.NotNull(c.Titulo); Assert.Contains("MCP/agente:", c.Proveniencia.Origem); Assert.Equal(Dante.Domain.CapturaDeConhecimento.EstadoDoCandidato.Pendente, c.Estado); });
            }
            // Confirmação pelo fluxo natural de negócio, independente de auto-approval da CLI.
            Assert.Contains("Candidatos pendentes", await brain.AtenderAsync(BrainMcpTests.Mensagem("listar candidatos", 124), "listar candidatos"));
            Assert.Contains("consolidado", await brain.AtenderAsync(BrainMcpTests.Mensagem("confirmar primeira", 125), "confirmar primeira"));
            var consultar = BrainMcpTests.Mensagem("Consulte o Brain sobre este assunto e mostre o histórico completo de proveniência do conhecimento. Responda conteúdo e referência da fonte.", 126);
            app.GetRequiredService<RegistroDeOperacoesBrain>().Observar(consultar);
            var novosServidores = await ferramentas.ForSessionAsync("LIVE2", new(42, agente, Dante.Application.Contextos.JobExecutionContext.General(root), BrainConversation: consultar.ParaFerramentas()), timeout.Token);
            string conteudo; await using (var db = banco.Contexto()) conteudo = (await db.Conhecimentos.SingleAsync()).Conteudo!;
            await using (var driver = Driver(agente))
            {
                await driver.StartAsync(new(root, IsGeneral: true, Profile: AgentPermissionProfile.Auto, ToolServers: novosServidores), timeout.Token);
                await using var eventos = driver.ReadEventsAsync(timeout.Token).GetAsyncEnumerator();
                await driver.StartTurnAsync(consultar.Text! + " Assunto: " + conteudo, timeout.Token);
                var usadas = new List<string>();
                var resposta = await RespostaAsync(driver, eventos, timeout.Token, usadas); output.WriteLine(resposta);
                Assert.DoesNotContain(usadas, x => x.Contains("plugin", StringComparison.OrdinalIgnoreCase));
                Assert.Contains("conversa:", resposta); Assert.Contains(usadas, x => x.Contains("brain_mostrar_origem", StringComparison.Ordinal));
            }
            ferramentas.EndSession("LIVE2");
        }
        finally { Directory.Delete(root, true); }
    }
    [LiveBrainTheory]
    [InlineData(AgentKind.Codex)]
    [InlineData(AgentKind.Claude)]
    public async Task AgenteInvestigaRepositorioECapturaInferenciaQueOutraSessaoRecupera(AgentKind agente)
    {
        await using var banco = await Banco.CriarAsync(); using var app = BrainMcpTests.CriarApp(banco.ConnectionString);
        var brain = app.GetRequiredService<TelegramBrain>(); await BrainMcpTests.SelecionarAsync(brain);
        using var ferramentas = BrainMcpTests.Provedor(app); using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var root = Directory.CreateTempSubdirectory("dante-live-brain-repo-").FullName;
        var fato = "O backend usa portas de aplicação e adapters HTTP. Identificador verificável: " + Guid.NewGuid().ToString("N");
        try
        {
            var init = new System.Diagnostics.ProcessStartInfo("git") { UseShellExecute = false };
            init.ArgumentList.Add("init"); init.ArgumentList.Add("--quiet"); init.ArgumentList.Add(root);
            using (var git = System.Diagnostics.Process.Start(init)!) { await git.WaitForExitAsync(timeout.Token); Assert.Equal(0, git.ExitCode); }
            await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "# Cognexa (fixture de teste)\n\n" + fato, timeout.Token);
            var pedido = "Analise o README.md deste repositório e registre no Brain um conhecimento chamado Arquitetura do Cognexa " +
                "sobre a arquitetura descrita. Inclua literalmente o fato e seu identificador verificável do README no conteúdo, " +
                "registre como conclusão da sua análise com referência a README.md. Não confirme ainda.";
            var mensagem = BrainMcpTests.Mensagem(pedido);
            var servidores = await ferramentas.ForSessionAsync("REPO1", new(42, agente,
                Dante.Application.Contextos.JobExecutionContext.Repository("cognexa-fixture", root), BrainConversation: mensagem.ParaFerramentas()), timeout.Token);
            await using (var driver = Driver(agente))
            {
                await driver.StartAsync(new(root, Profile: AgentPermissionProfile.Auto, ToolServers: servidores), timeout.Token);
                await using var eventos = driver.ReadEventsAsync(timeout.Token).GetAsyncEnumerator();
                await driver.StartTurnAsync(pedido, timeout.Token);
                var usadas = new List<string>(); output.WriteLine(await RespostaAsync(driver, eventos, timeout.Token, usadas));
                Assert.Contains(usadas, x => x.Contains("brain_capturar_conhecimento", StringComparison.Ordinal));
                SemDescobertaExterna(usadas);
            }
            await using (var db = banco.Contexto())
            {
                var candidato = await db.CandidatosDeConhecimento.SingleAsync();
                Assert.Equal("Arquitetura do Cognexa", candidato.Titulo); Assert.Contains(fato, candidato.Conteudo);
                Assert.Equal(Dante.Domain.CapturaDeConhecimento.NaturezaDoConteudo.ConclusaoDoAgente, candidato.Natureza);
                Assert.Equal(Dante.Domain.Conhecimentos.TipoDeConhecimento.Inferencia, candidato.Tipo);
                Assert.Contains("README", candidato.Justificativa, StringComparison.OrdinalIgnoreCase);
            }
            Assert.Contains("Candidatos pendentes", await brain.AtenderAsync(BrainMcpTests.Mensagem("listar candidatos", 124), "listar candidatos"));
            Assert.Contains("consolidado", await brain.AtenderAsync(BrainMcpTests.Mensagem("confirmar primeira", 125), "confirmar primeira"));
            ferramentas.EndSession("REPO1");
            var consulta = BrainMcpTests.Mensagem("O que o Brain sabe sobre a arquitetura do Cognexa? Mostre o conhecimento completo, a referência de sua origem e o histórico completo de proveniência.", 126);
            var novos = await ferramentas.ForSessionAsync("REPO2", new(42, agente,
                Dante.Application.Contextos.JobExecutionContext.General(root), BrainConversation: consulta.ParaFerramentas()), timeout.Token);
            await using (var driver = Driver(agente))
            {
                await driver.StartAsync(new(root, IsGeneral: true, Profile: AgentPermissionProfile.Auto, ToolServers: novos), timeout.Token);
                await using var eventos = driver.ReadEventsAsync(timeout.Token).GetAsyncEnumerator();
                await driver.StartTurnAsync(consulta.Text!, timeout.Token);
                var usadas = new List<string>(); var resposta = await RespostaAsync(driver, eventos, timeout.Token, usadas); output.WriteLine(resposta);
                Assert.Contains(fato.Split(": ")[1], resposta);
                Assert.Contains("conversa:", resposta);
                Assert.Contains(usadas, x => x.Contains("brain_buscar_conhecimento", StringComparison.Ordinal));
                Assert.Contains(usadas, x => x.Contains("brain_mostrar_origem", StringComparison.Ordinal));
                SemDescobertaExterna(usadas);
            }
            ferramentas.EndSession("REPO2");
        }
        finally { Directory.Delete(root, true); }
    }
    private static void SemDescobertaExterna(IEnumerable<string> ferramentas)
    {
        Assert.DoesNotContain(ferramentas, x => x.Contains("plugin", StringComparison.OrdinalIgnoreCase) ||
            x.Contains("connector", StringComparison.OrdinalIgnoreCase) || x.Contains("tool_search", StringComparison.OrdinalIgnoreCase) ||
            x.Contains("tools_search", StringComparison.OrdinalIgnoreCase));
    }

    private static IAgentSessionDriver Driver(AgentKind agente)
    {
        var launcher = new InteractiveAgentProcessLauncher(new AgentExecutableResolver());
        return agente == AgentKind.Codex ? new CodexSessionDriver(launcher) : new ClaudeSessionDriver(launcher);
    }
    private async Task<string> RespostaAsync(IAgentSessionDriver driver, IAsyncEnumerator<AgentEvent> eventos, CancellationToken ct, List<string>? usadas = null)
    {
        var texto = new StringBuilder();
        while (await eventos.MoveNextAsync())
        {
            output.WriteLine("Evento: " + eventos.Current.GetType().Name);
            switch (eventos.Current)
            {
                case ToolStartedEvent t: usadas?.Add(t.Description); output.WriteLine("Ferramenta: " + t.Description); break;
                case MessageCompletedEvent m: texto.Append(m.Text); break;
                case UserInputRequestedEvent pergunta: throw new InvalidOperationException("Agente solicitou esclarecimento para fixture inequívoca: " + string.Join("; ", pergunta.Questions.Select(q => q.Text)));
                case ErrorEvent erro: throw new InvalidOperationException(erro.Message);
                case ApprovalRequestedEvent a: await driver.RespondAsync(a.UpstreamRequestId, new AgentApprovalResponse(AgentApprovalDecision.ApproveOnce), ct); break;
                case TurnCompletedEvent fim: Assert.Equal(AgentTurnOutcome.Completed, fim.Outcome); return texto.ToString();
            }
        }
        throw new InvalidOperationException("Sessão terminou antes da resposta.");
    }
    private sealed class LiveBrainTheoryAttribute : TheoryAttribute
    {
        public LiveBrainTheoryAttribute()
        {
            if (Environment.GetEnvironmentVariable("DANTE_LIVE_BRAIN_MCP") != "1" || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DANTE_TEST_POSTGRES")))
                Skip = "Defina DANTE_LIVE_BRAIN_MCP=1 e DANTE_TEST_POSTGRES para CLI real e banco temporário.";
        }
    }
}
