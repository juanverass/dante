using Dante.Worker.Sessions;

namespace Dante.Worker.Execucoes;

// Recebe os eventos da sessão reservada de uma etapa de Tarefa (AD-68) no lugar do Telegram: guarda a última mensagem
// concluída do turno e o seu desfecho. A tarefa é somente leitura (plan): todo pedido de aprovação é negado na hora,
// pelo próprio executor; nada espera um humano que não vê a sessão.
public sealed class ObservadorDaTarefa(SessionRegistry sessoes, long idDoDono) : IAgentSessionEventSink
{
    public const string MotivoDaNegacao = "Tarefa somente leitura: nenhuma ação que exija aprovação é concedida.";

    private readonly object gate = new();
    private readonly TaskCompletionSource<(AgentTurnOutcome Desfecho, string? Erro)> turno =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? ultimaMensagem;

    // Concluída com o primeiro turno encerrado (ou com a falha da sessão).
    public Task<(AgentTurnOutcome Desfecho, string? Erro)> Turno => turno.Task;

    public string? UltimaMensagem
    {
        get { lock (gate) return ultimaMensagem; }
    }

    public int AprovacoesNegadas { get; private set; }

    public Task PublishAsync(AgentSessionSnapshot session, AgentEvent agentEvent, CancellationToken cancellationToken)
    {
        switch (agentEvent)
        {
            case MessageCompletedEvent mensagem when !string.IsNullOrWhiteSpace(mensagem.Text):
                lock (gate) ultimaMensagem = mensagem.Text;
                break;
            case ApprovalRequestedEvent aprovacao:
                AprovacoesNegadas++;
                // Fora do laço de eventos: responder espera a mesma serialização que publica este evento.
                _ = Task.Run(() => sessoes.RespondReservedAsync(idDoDono, aprovacao.RequestId,
                    new AgentApprovalResponse(AgentApprovalDecision.Deny, MotivoDaNegacao), CancellationToken.None),
                    CancellationToken.None);
                break;
            case TurnCompletedEvent fim:
                turno.TrySetResult((fim.Outcome, fim.Error));
                break;
            case ErrorEvent erro when agentEvent.TurnId is null:
                // Falha da sessão inteira: não haverá fim de turno.
                turno.TrySetResult((AgentTurnOutcome.Failed, erro.Message));
                break;
        }
        return Task.CompletedTask;
    }
}
