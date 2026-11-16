using Dante.Worker.Sessions;

namespace Dante.Worker.Execucoes;

// Encerramento ordenado do espelho (AD-66). Registrado antes dos demais hosted services, para no fim: o polling já
// esperou os jobs, então aqui as sessões vivas são interrompidas e a fila é gravada enquanto o provider ainda cria
// escopos. Depois do StopAsync, o host descarta o provider e o espelho não consegue mais gravar.
public sealed class EncerramentoDoLedger(SessionRegistry sessoes, EspelhoDoLedger espelho,
    ILogger<EncerramentoDoLedger> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await sessoes.DisposeAsync();
        try
        {
            await espelho.DescarregarAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Prazo de encerramento esgotado antes de gravar todo o ledger.");
        }
    }
}
