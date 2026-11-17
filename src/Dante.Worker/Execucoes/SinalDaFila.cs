namespace Dante.Worker.Execucoes;

// Sinal em memória que acorda o processador da fila (AD-67): trabalho enfileirado neste processo, notificação do banco
// ou slot liberado. Vários sinais antes de o processador acordar valem por um; perder um sinal só atrasa até o polling.
public sealed class SinalDaFila
{
    private readonly SemaphoreSlim sinal = new(0, 1);

    public void Sinalizar()
    {
        try
        {
            sinal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Já sinalizado.
        }
    }

    // Verdadeiro quando sinalizado antes do prazo.
    public Task<bool> EsperarAsync(TimeSpan prazo, CancellationToken cancellationToken) =>
        sinal.WaitAsync(prazo < TimeSpan.Zero ? TimeSpan.Zero : prazo, cancellationToken);
}
