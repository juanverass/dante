using System.Threading.Channels;
using Dante.Application.Execucoes;

namespace Dante.Worker.Execucoes;

// Espelho dos jobs e das sessões no ledger de execuções (AD-66, fase 1a). Só existe com banco configurado e
// Orquestracao:Habilitada; sem ele, JobRegistry e SessionRegistry funcionam exatamente como antes.
// O runtime nunca espera o banco além da reserva de um número: os registros vão para uma fila em memória, gravada
// em ordem por um único consumidor, cada um num escopo próprio. Falha de gravação é logada e não interrompe job nem
// sessão; fila cheia descarta o registro com aviso.
public sealed class EspelhoDoLedger : IAsyncDisposable, IDisposable
{
    internal const int Capacidade = 1000;
    private static readonly TimeSpan PrazoDaReserva = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PausaAposFalha = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PrazoDoEncerramento = TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory escopos;
    private readonly ILogger<EspelhoDoLedger> logger;
    private readonly TimeProvider relogio;
    private readonly Channel<Registro> fila = Channel.CreateBounded<Registro>(new BoundedChannelOptions(Capacidade)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly CancellationTokenSource encerramento = new();
    private readonly Task consumidor;
    private long reservaIndisponivelAte;

    public EspelhoDoLedger(IServiceScopeFactory escopos, ILogger<EspelhoDoLedger> logger, TimeProvider? relogio = null)
    {
        this.escopos = escopos;
        this.logger = logger;
        this.relogio = relogio ?? TimeProvider.System;
        consumidor = Task.Run(ConsumirAsync);
    }

    public DateTimeOffset Agora => relogio.GetUtcNow();

    // Número J…/E… da sequência do banco, acima do maior já usado localmente; nulo quando o banco não respondeu
    // (o chamador usa o contador local).
    public Task<long?> ReservarNumeroDeExecucaoAsync(long acimaDe, CancellationToken cancellationToken) =>
        ReservarAsync((ledger, token) => ledger.ReservarNumeroDeExecucaoAsync(acimaDe, token), cancellationToken);

    // Número S… da sequência do banco, acima do maior já usado localmente; nulo quando o banco não respondeu.
    public Task<long?> ReservarNumeroDeSessaoAsync(long acimaDe, CancellationToken cancellationToken) =>
        ReservarAsync((ledger, token) => ledger.ReservarNumeroDeSessaoAsync(acimaDe, token), cancellationToken);

    // Enfileira sem esperar o banco. A descrição vai para o log em caso de falha; nunca contém dados do usuário.
    public void Registrar(string descricao, Func<ILedgerDeExecucoesAppService, CancellationToken, Task> operacao)
    {
        if (!fila.Writer.TryWrite(new Registro(descricao, operacao, null)))
            logger.LogWarning("Registro {Descricao} descartado: fila do ledger cheia ou encerrada.", descricao);
    }

    // Espera tudo o que foi enfileirado até agora ser gravado (ou falhar). Com a fila cheia, espera espaço para a
    // marca de descarga em vez de retornar: o encerramento não pode seguir com registros aceitos ainda pendentes.
    public async Task DescarregarAsync(CancellationToken cancellationToken = default)
    {
        var concluido = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await fila.Writer.WriteAsync(new Registro("descarga", null, concluido), cancellationToken);
        }
        catch (ChannelClosedException)
        {
            // Já encerrado: o DisposeAsync aguarda o consumidor.
            return;
        }
        await concluido.Task.WaitAsync(cancellationToken);
    }

    // Encerramento do host: grava o que já está na fila (inclusive as sessões interrompidas pelo próprio
    // encerramento), com prazo.
    public async ValueTask DisposeAsync()
    {
        if (!fila.Writer.TryComplete()) return;
        try
        {
            await consumidor.WaitAsync(PrazoDoEncerramento).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Registros do ledger pendentes no encerramento foram abandonados.");
            await encerramento.CancelAsync().ConfigureAwait(false);
        }
        encerramento.Dispose();
    }

    // Host.Run descarta o provider de forma síncrona. O SessionRegistry depende do espelho e é descartado antes dele,
    // então as sessões interrompidas pelo encerramento já estão na fila.
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private async Task<long?> ReservarAsync(Func<ILedgerDeExecucoesAppService, CancellationToken, Task<long>> reservar,
        CancellationToken cancellationToken)
    {
        // Depois de uma falha, o banco não é consultado por um tempo: conversa e jobs não esperam a cada mensagem.
        if (relogio.GetUtcNow().UtcTicks < Interlocked.Read(ref reservaIndisponivelAte)) return null;
        using var prazo = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        prazo.CancelAfter(PrazoDaReserva);
        try
        {
            await using var escopo = escopos.CreateAsyncScope();
            return await reservar(escopo.ServiceProvider.GetRequiredService<ILedgerDeExecucoesAppService>(), prazo.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref reservaIndisponivelAte, (relogio.GetUtcNow() + PausaAposFalha).UtcTicks);
            logger.LogWarning("Ledger indisponível para reservar número; seguindo com o contador local ({ErrorType}).",
                exception.GetType().Name);
            return null;
        }
    }

    private async Task ConsumirAsync()
    {
        await foreach (var registro in fila.Reader.ReadAllAsync())
        {
            if (registro.Concluido is { } concluido)
            {
                concluido.TrySetResult();
                continue;
            }
            try
            {
                await using var escopo = escopos.CreateAsyncScope();
                await registro.Operacao!(escopo.ServiceProvider.GetRequiredService<ILedgerDeExecucoesAppService>(),
                    encerramento.Token);
            }
            catch (Exception exception)
            {
                // Exceptions de provider podem conter dados: só o tipo vai para o log.
                logger.LogWarning("Falha ao gravar {Descricao} no ledger ({ErrorType}).", registro.Descricao,
                    exception.GetType().Name);
            }
        }
    }

    private sealed record Registro(string Descricao,
        Func<ILedgerDeExecucoesAppService, CancellationToken, Task>? Operacao, TaskCompletionSource? Concluido);
}
