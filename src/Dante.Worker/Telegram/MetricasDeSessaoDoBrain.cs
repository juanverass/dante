using System.Collections.Concurrent;
using System.Text;
using Dante.Worker.Sessions;
namespace Dante.Worker.Telegram;

// Mede a resposta de cada turno (#148) para o histórico bruto de referência: só o tamanho do texto e o uso informado
// pela CLI, nunca o conteúdo. Repassa todo evento ao sink seguinte antes de medir.
public sealed class MetricasDeSessaoDoBrain(IAgentSessionEventSink proximo, IContinuidadeDoBrain continuidade) : IAgentSessionEventSink
{
    private readonly ConcurrentDictionary<string, long> respostas = new(StringComparer.Ordinal);

    public async Task PublishAsync(AgentSessionSnapshot session, AgentEvent agentEvent, CancellationToken cancellationToken)
    {
        await proximo.PublishAsync(session, agentEvent, cancellationToken);
        if (!continuidade.Configurado) return;
        switch (agentEvent)
        {
            case MessageCompletedEvent message:
                var bytes = Encoding.UTF8.GetByteCount(message.Text);
                respostas.AddOrUpdate(session.Id, bytes, (_, total) => total + bytes);
                break;
            case TurnCompletedEvent completed:
                respostas.TryRemove(session.Id, out var resposta);
                await continuidade.RegistrarTurnoAsync(session.Id, session.Agent.ToString(),
                    (int)Math.Min(int.MaxValue, (resposta + 2) / 3), completed.Outcome, completed.Usage, cancellationToken);
                break;
        }
    }
}
