using System.Collections.Concurrent;

namespace Dante.Application.Execucoes;

// Atalho em memória do pedido de cancelamento (#305): o processo que tem a posse de uma execução registra aqui o seu
// token, e o caso de uso do cancelamento o sinaliza depois de gravar o evento CancelamentoSolicitado. É só otimização:
// o evento persistido é a fonte do pedido, conferido por quem tem a posse antes de iniciar, a cada heartbeat e antes
// de concluir; um sinal perdido (outro processo, registro tardio) só atrasa o cancelamento.
public sealed class CanalDeCancelamento
{
    private readonly ConcurrentDictionary<long, CancellationTokenSource> emExecucao = new();

    public IDisposable Registrar(long numeroDaExecucao, CancellationTokenSource cancelamento)
    {
        ArgumentNullException.ThrowIfNull(cancelamento);
        emExecucao[numeroDaExecucao] = cancelamento;
        return new Registro(this, numeroDaExecucao, cancelamento);
    }

    // Verdadeiro quando havia um token registrado para a execução neste processo.
    public bool Sinalizar(long numeroDaExecucao)
    {
        if (!emExecucao.TryGetValue(numeroDaExecucao, out var cancelamento)) return false;
        try
        {
            cancelamento.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        return true;
    }

    private sealed class Registro(CanalDeCancelamento canal, long numero, CancellationTokenSource cancelamento) : IDisposable
    {
        public void Dispose() => canal.emExecucao.TryRemove(new KeyValuePair<long, CancellationTokenSource>(numero, cancelamento));
    }
}
