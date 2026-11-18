using Dante.Worker.Sessions;

namespace Dante.Worker.Execucoes;

// Eventos da sessão reservada de um passo de Roteiro (#308) no lugar do Telegram: a última mensagem e o desfecho de
// cada turno, um turno por vez (a revisão pode receber um lembrete de registrar o veredito). Nenhuma aprovação
// interativa é concedida: a escrita confinada e a revisão confinada rodam com approvalPolicy never, e um pedido que
// chegue mesmo assim é negado na hora.
public sealed class ObservadorDoRoteiro(SessionRegistry sessoes, long idDoDono) : IAgentSessionEventSink
{
    public const string MotivoDaNegacao = "Passo de roteiro: nenhuma ação que exija aprovação é concedida.";

    private readonly object gate = new();
    private TaskCompletionSource<(AgentTurnOutcome Desfecho, string? Erro)> turno = Novo();
    private string? ultimaMensagem;

    public Task<(AgentTurnOutcome Desfecho, string? Erro)> Turno
    {
        get { lock (gate) return turno.Task; }
    }

    public string? UltimaMensagem
    {
        get { lock (gate) return ultimaMensagem; }
    }

    public int AprovacoesNegadas { get; private set; }

    // Antes de enviar o próximo turno: a espera seguinte é dele.
    public void ProximoTurno()
    {
        lock (gate)
        {
            if (turno.Task.IsCompleted) turno = Novo();
            ultimaMensagem = null;
        }
    }

    public Task PublishAsync(AgentSessionSnapshot session, AgentEvent agentEvent, CancellationToken cancellationToken)
    {
        switch (agentEvent)
        {
            case MessageCompletedEvent mensagem when !string.IsNullOrWhiteSpace(mensagem.Text):
                lock (gate) ultimaMensagem = mensagem.Text;
                break;
            case ApprovalRequestedEvent aprovacao:
                AprovacoesNegadas++;
                _ = Task.Run(() => sessoes.RespondReservedAsync(idDoDono, aprovacao.RequestId,
                    new AgentApprovalResponse(AgentApprovalDecision.Deny, MotivoDaNegacao), CancellationToken.None),
                    CancellationToken.None);
                break;
            case TurnCompletedEvent fim:
                lock (gate) turno.TrySetResult((fim.Outcome, fim.Error));
                break;
            case ErrorEvent erro when agentEvent.TurnId is null:
                lock (gate) turno.TrySetResult((AgentTurnOutcome.Failed, erro.Message));
                break;
        }
        return Task.CompletedTask;
    }

    private static TaskCompletionSource<(AgentTurnOutcome, string?)> Novo() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
