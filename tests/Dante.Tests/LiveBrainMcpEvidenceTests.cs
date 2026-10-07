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
            var pedido = "Use SOMENTE as ferramentas dante_brain. Consulte o escopo. Registre cada um dos três itens abaixo como candidato SEPARADO, preserve conteúdo e título exatos, tags dante. Não confirme. Não use arquivos, terminal ou outras ferramentas.\n" +
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
                await driver.StartTurnAsync(pedido, timeout.Token); output.WriteLine(await RespostaAsync(driver, eventos, timeout.Token));
                var duplicado = BrainMcpTests.Mensagem("Use brain_capturar_conhecimento para este mesmo conteúdo e reporte a possível duplicidade retornada, sem confirmar: D.A.N.T.E. significa Distributed Agent Network for Task Execution.", 130);
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
            var consultar = BrainMcpTests.Mensagem("Use dante_brain para buscar o conhecimento consolidado por palavras do assunto e mostrar a origem dele. Use somente essas ferramentas, sem terminal/arquivos. Responda conteúdo e referência da fonte.", 126);
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
                Assert.Contains("conversa:", resposta); Assert.Contains(usadas, x => x.Contains("brain_mostrar_origem", StringComparison.Ordinal));
            }
            ferramentas.EndSession("LIVE2");
        }
        finally { Directory.Delete(root, true); }
    }
    private static IAgentSessionDriver Driver(AgentKind agente)
    {
        var launcher = new InteractiveAgentProcessLauncher(new AgentExecutableResolver());
        return agente == AgentKind.Codex ? new CodexSessionDriver(launcher) : new ClaudeSessionDriver(launcher);
    }
    private static async Task<string> RespostaAsync(IAgentSessionDriver driver, IAsyncEnumerator<AgentEvent> eventos, CancellationToken ct, List<string>? usadas = null)
    {
        var texto = new StringBuilder();
        while (await eventos.MoveNextAsync())
        {
            switch (eventos.Current)
            {
                case ToolStartedEvent t: usadas?.Add(t.Description); break;
                case MessageCompletedEvent m: texto.Append(m.Text); break;
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
