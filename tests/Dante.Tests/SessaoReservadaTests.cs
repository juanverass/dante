using Dante.Application.Agentes;
using Dante.Worker.Execucoes;
using Dante.Worker.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dante.Tests;

// #301: a sessão de uma etapa de Tarefa é reservada ao executor. Ela não vira a sessão ativa do dono (mensagens comuns
// seguem o caminho de sempre), seus eventos não chegam ao sink do Telegram e nenhum comando do dono a alcança; pedidos
// de aprovação são negados pelo próprio executor (somente leitura). Sem observador, nada muda.
public sealed class SessaoReservadaTests : IAsyncDisposable
{
    private readonly FakeSessionDriverFactory drivers = new();
    private readonly RecordingSessionSink sink = new();
    private readonly SessionRegistry sessions;

    public SessaoReservadaTests() =>
        sessions = new SessionRegistry(drivers, NullLogger<SessionRegistry>.Instance, sink);

    [Fact]
    public async Task SessaoReservadaNaoViraAtivaNemFalaComOTelegramNemRecebeComandosDoDono()
    {
        var comum = await sessions.StartAsync(new SessionStartRequest(123, AgentKind.Codex, JobExecutionContext.General("/g")));
        Assert.True(comum.Accepted);
        var observador = new ObservadorDaTarefa(sessions, 123);
        var reservada = await sessions.StartAsync(new SessionStartRequest(123, AgentKind.Claude, JobExecutionContext.General("/g"),
            Profile: AgentPermissionProfile.Plan, Observer: observador));
        Assert.True(reservada.Accepted, reservada.Error);
        var id = reservada.Session!.Id;

        // A ativa continua a comum: mensagem comum não vai para a tarefa.
        Assert.Equal(comum.Session!.Id, sessions.GetActive(123)!.Id);

        // Comandos do dono não alcançam a reservada.
        Assert.Contains("executa uma tarefa", (await sessions.SubmitAsync(123, id, new AgentInput("oi", []))).Error);
        Assert.Contains("executa uma tarefa", sessions.Select(123, id).Error);
        Assert.Contains("executa uma tarefa", (await sessions.CloseAsync(123, id)).Error);
        Assert.Contains("executa uma tarefa", (await sessions.InterruptAsync(123, id)).Error);
        Assert.Contains("executa uma tarefa",
            (await sessions.ChangeModeAsync(123, id, AgentPermissionProfile.Auto)).Error);

        // O executor envia o turno; os eventos vão só para o observador.
        var enviado = await sessions.SubmitReservedAsync(123, id, new AgentInput("leia", []));
        Assert.NotEqual(SubmitOutcome.Rejected, enviado.Outcome);
        var driver = drivers.Created[1];
        driver.Emit(new ApprovalRequestedEvent("up-1", AgentToolKind.Command, "rm -rf /"));
        await Eventually(() => driver.Responses.Count == 1);
        Assert.Equal(AgentApprovalDecision.Deny, Assert.IsType<AgentApprovalResponse>(driver.Responses[0].Response).Decision);
        driver.Emit(new MessageCompletedEvent("m1", "resposta"));
        driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
        var (desfecho, _) = await observador.Turno.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((AgentTurnOutcome.Completed, "resposta", 1), (desfecho, observador.UltimaMensagem, observador.AprovacoesNegadas));
        Assert.DoesNotContain(sink.Published, publicado => publicado.Session.Id == id);

        // O dono não responde pedidos da reservada; o executor fecha a sessão.
        Assert.False((await sessions.RespondAsync(123, "R-qualquer", new AgentApprovalResponse(AgentApprovalDecision.ApproveOnce))).Accepted);
        Assert.True((await sessions.CloseReservedAsync(123, id)).Accepted);
        Assert.Equal(comum.Session.Id, sessions.GetActive(123)!.Id);

        // A comum continua exatamente como antes: comandos do dono funcionam; o caminho reservado não a alcança.
        Assert.NotEqual(SubmitOutcome.Rejected, (await sessions.SubmitAsync(123, null, new AgentInput("olá", []))).Outcome);
        Assert.Contains("não é reservada", (await sessions.SubmitReservedAsync(123, comum.Session.Id, new AgentInput("x", []))).Error);
        drivers.Created[0].Emit(new MessageCompletedEvent("m2", "comum"));
        await Eventually(() => sink.Published.Any(publicado => publicado.Session.Id == comum.Session.Id));
    }

    private static async Task Eventually(Func<bool> condicao)
    {
        for (var i = 0; i < 200 && !condicao(); i++) await Task.Delay(25);
        Assert.True(condicao());
    }

    public async ValueTask DisposeAsync() => await sessions.DisposeAsync();
}
