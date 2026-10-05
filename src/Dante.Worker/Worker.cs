using Dante.Infrastructure.Contextos;

namespace Dante.Worker;

public sealed class Worker(ILogger<Worker> logger, AssistantSettingsStore settings) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("DANTE iniciado. Agente padrão: {DefaultAgent}.", settings.Current.DefaultAgent);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("DANTE encerrado.");
        }
    }
}
