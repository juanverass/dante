using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Dante.Application.Planilhas;
using Dante.Infrastructure.Agentes;
using Dante.Worker.Planilhas;
using Dante.Worker.Sessions;
using Dante.Worker.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Dante.Tests;

// Agentes reais + servidor MCP/Application/adapters reais, Google emulado e Telegram gravado.
// Relay stdio/loopback só de teste permite ao processo MCP acessar a composição isolada em memória.
public sealed class LiveDescobertaDePlanilhasTests(ITestOutputHelper output)
{
    [LivePlanilhasTheory]
    [InlineData(AgentKind.Claude)]
    [InlineData(AgentKind.Codex)]
    public async Task PedidoNaturalDescobreFonteSemDicaTecnica(AgentKind agente)
    {
        using var ambiente = new AmbienteDePlanilhas();
        ambiente.Google.Titulo = "Registros pessoais";
        ambiente.Google.AdicionarAba("Registros", 100, 8);
        ambiente.Google.Definir("Registros", "B3", "Item");
        ambiente.Google.Definir("Registros", "C3", "Prazo");
        ambiente.Google.Definir("Registros", "B4", "Renovar cadastro Aurora");
        ambiente.Google.Definir("Registros", "C4", "2026-10-08");
        ambiente.Google.Definir("Registros", "B5", "Revisar cadastro Boreal");
        ambiente.Google.Definir("Registros", "C5", "2026-10-09");
        await ambiente.CadastrarAsync("pendencias", "Itens pessoais organizados por prazo");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = timeout.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var porta = ((IPEndPoint)listener.LocalEndpoint).Port;
        var relay = Path.Combine(ambiente.Diretorio, "relay.py");
        await File.WriteAllTextAsync(relay, """
            import socket, sys, threading
            s = socket.create_connection(('127.0.0.1', int(sys.argv[1])))
            def entrada():
                for linha in sys.stdin.buffer:
                    s.sendall(linha)
                s.shutdown(socket.SHUT_WR)
            threading.Thread(target=entrada, daemon=True).start()
            with s.makefile('rb') as r:
                for linha in r:
                    sys.stdout.buffer.write(linha)
                    sys.stdout.buffer.flush()
            """, ct);
        var chamadas = new List<string>();
        var leituras = new List<string>();
        var servidor = ServidorMcpDePlanilhasTests.Servidor(ambiente, agente.ToString());
        var ponte = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false), leaveOpen: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            // O servidor real mantém regras de descoberta e proteções de escrita.
            await servidor.ExecutarAsync(new LeitorObservado(reader, leituras), writer, ct);
        }, ct);
        var servers = new[] { new AgentToolServer("dante_planilhas", "/usr/bin/python3",
            [relay, porta.ToString(System.Globalization.CultureInfo.InvariantCulture)], new Dictionary<string, string>(),
            ServidorMcpDePlanilhas.FerramentasDeLeitura) };
        var launcher = new InteractiveAgentProcessLauncher(new AgentExecutableResolver());
        await using IAgentSessionDriver driver = agente == AgentKind.Claude
            ? new ClaudeSessionDriver(launcher) : new CodexSessionDriver(launcher);
        var api = new ApiDeConversa();
        var delivery = new TelegramDeliveryService(api, NullLogger<TelegramDeliveryService>.Instance);
        var session = ApiDeConversa.Sessao(ambiente.Diretorio, agente);
        delivery.RegisterSession(session.Id, 123, 123, false);
        delivery.SetActiveSession(123, session.Id);
        try
        {
            await driver.StartAsync(new(ambiente.Diretorio, IsGeneral: true, Profile: AgentPermissionProfile.Auto,
                ToolServers: servers), ct);
            await driver.StartTurnAsync("Quais são minhas pendências para 8 de outubro de 2026? Liste apenas as desse dia.", ct);
            var resposta = new StringBuilder();
            await foreach (var evento in driver.ReadEventsAsync(ct))
            {
                await delivery.PublishAsync(session, evento with { SessionId = session.Id, TurnId = "T1" }, ct);
                switch (evento)
                {
                    case ToolStartedEvent tool:
                        chamadas.Add(tool.ToolName ?? tool.Description);
                        output.WriteLine("Ferramenta: " + tool.Description);
                        break;
                    case MessageCompletedEvent m: resposta.Append(m.Text); break;
                    case ApprovalRequestedEvent a:
                        await driver.RespondAsync(a.UpstreamRequestId,
                            new AgentApprovalResponse(AgentApprovalDecision.ApproveOnce), ct); break;
                    case UserInputRequestedEvent: Assert.Fail("Fixture inequívoca não exige localização manual."); break;
                    case ErrorEvent e: Assert.Fail(e.Message); break;
                    case TurnCompletedEvent fim:
                        Assert.Equal(AgentTurnOutcome.Completed, fim.Outcome);
                        await api.AguardarAsync(delivery, session.Id);
                        output.WriteLine("Resposta: " + resposta);
                        Assert.Contains("Renovar cadastro Aurora", resposta.ToString());
                        Assert.DoesNotContain("Revisar cadastro Boreal", resposta.ToString());
                        Assert.Equal("listar_planilhas", chamadas.First());
                        Assert.Contains("descrever_planilha", chamadas);
                        Assert.Contains("ler_intervalo", chamadas);
                        Assert.True(chamadas.IndexOf("listar_planilhas") < chamadas.IndexOf("descrever_planilha"));
                        Assert.True(chamadas.IndexOf("descrever_planilha") < chamadas.IndexOf("ler_intervalo"));
                        Assert.DoesNotContain("Brain", api.Texto, StringComparison.OrdinalIgnoreCase);
                        Assert.DoesNotContain("dante_planilhas", api.Texto);
                        Assert.DoesNotContain("mcp__", api.Texto);
                        Assert.DoesNotContain("MCP", api.Texto);
                        Assert.DoesNotContain("Você tem razão", api.Texto, StringComparison.OrdinalIgnoreCase);
                        Assert.DoesNotContain("—", api.Texto);
                        Assert.Contains("Buscando suas planilhas...", api.Texto);
                        Assert.Contains("Lendo as informações necessárias...", api.Texto);
                        Assert.Empty(ambiente.Google.Escritas);
                        Assert.NotEmpty(leituras);
                        Assert.All(leituras, intervalo =>
                        {
                            var area = IntervaloA1.Interpretar(intervalo);
                            Assert.True(area.QuantidadeDeCelulas <= 100, "Agente deve ler só região pequena: " + intervalo);
                        });
                        return;
                }
            }
            Assert.Fail("Agente encerrou sem resposta.");
        }
        finally
        {
            await driver.CloseAsync(CancellationToken.None);
            timeout.Cancel();
            listener.Stop();
            try { await ponte; }
            catch (Exception e) when (e is OperationCanceledException or IOException or SocketException) { }
        }
    }

    private sealed class LeitorObservado(TextReader reader, List<string> leituras) : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken ct)
        {
            var linha = await reader.ReadLineAsync(ct);
            if (linha is not null && JsonNode.Parse(linha) is JsonObject mensagem &&
                (string?)mensagem["method"] == "tools/call" &&
                (string?)mensagem["params"]?["name"] == "ler_intervalo" &&
                (string?)mensagem["params"]?["arguments"]?["intervalo"] is { } intervalo)
                leituras.Add(intervalo);
            return linha;
        }
    }

    private sealed class LivePlanilhasTheoryAttribute : TheoryAttribute
    {
        public LivePlanilhasTheoryAttribute()
        {
            if (Environment.GetEnvironmentVariable("DANTE_LIVE_PLANILHAS_AGENTES") != "1")
                Skip = "Defina DANTE_LIVE_PLANILHAS_AGENTES=1 para Claude/Codex reais e Google emulado (Linux/python3).";
        }
    }
}
