using Dante.Application.Execucoes;
using Dante.Worker.Telegram;

namespace Dante.Worker.Execucoes;

// Manutenção das aprovações de ação protegida (AD-72, #311, PR 2). Na partida, antes do polling do Telegram (registrado
// antes dele): o que estava EmExecucao quando o processo caiu vira Incerto (o efeito não é conhecido, nunca é repetido)
// e o que venceu expira. Depois, a cada minuto, expira os vencidos. O dono é avisado de cada um. As regras e as
// transições são da Application; aqui só o relógio da varredura e o aviso. Pressupõe um Worker por banco (paralelismo
// 1): outro processo vivo com uma execução em andamento teria a dele marcada Incerta.
public sealed class ManutencaoDasAprovacoesDeAcao(IServiceScopeFactory escopos, TelegramAprovacoesDeAcao avisos,
    ILogger<ManutencaoDasAprovacoesDeAcao> logger, TimeSpan? intervalo = null) : BackgroundService
{
    public static readonly TimeSpan IntervaloPadrao = TimeSpan.FromMinutes(1);

    private readonly TimeSpan intervalo = intervalo ?? IntervaloPadrao;
    private bool recuperado;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // A recuperação termina antes de os próximos serviços (o polling) começarem a aceitar decisões.
        await RecuperarAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var relogio = new PeriodicTimer(intervalo);
        try
        {
            while (await relogio.WaitForNextTickAsync(stoppingToken))
            {
                // Banco indisponível na partida: a recuperação é tentada de novo antes da expiração.
                if (!recuperado) await RecuperarAsync(stoppingToken);
                else await ExpirarAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal async Task RecuperarAsync(CancellationToken cancellationToken)
    {
        try
        {
            ManutencaoDeAprovacoesDto manutencao;
            await using (var escopo = escopos.CreateAsyncScope())
                manutencao = await escopo.ServiceProvider.GetRequiredService<IAprovacoesDeAcaoAppService>()
                    .RecuperarAposReinicioAsync(cancellationToken);
            recuperado = true;
            if (manutencao.Incertas.Count + manutencao.Expiradas.Count > 0)
                logger.LogWarning("Aprovações de ação recuperadas: {Incertas} incerta(s) por reinício, {Expiradas} expirada(s).",
                    manutencao.Incertas.Count, manutencao.Expiradas.Count);
            foreach (var aprovacao in manutencao.Incertas.Concat(manutencao.Expiradas))
                await avisos.AvisarAsync(aprovacao, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Recuperação das aprovações de ação falhou ({ErrorType}); nova tentativa na próxima rodada.",
                exception.GetType().Name);
        }
    }

    internal async Task ExpirarAsync(CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<AprovacaoDeAcaoDto> expiradas;
            await using (var escopo = escopos.CreateAsyncScope())
                expiradas = await escopo.ServiceProvider.GetRequiredService<IAprovacoesDeAcaoAppService>()
                    .ExpirarVencidasAsync(cancellationToken);
            foreach (var aprovacao in expiradas) await avisos.AvisarAsync(aprovacao, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Expiração das aprovações de ação falhou ({ErrorType}).", exception.GetType().Name);
        }
    }
}
