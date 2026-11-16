using Dante.Application.Execucoes;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Dante.Infrastructure.Fila;

// LISTEN no canal da fila (AD-67). O banco notifica no commit de todo trabalho que fica pendente (trigger da
// migration); a notificação é só um atalho para acordar o processador, que também faz polling. Conexão dedicada,
// fora do pool, sem transação; cai e reconecta sozinha.
public sealed class OuvinteDaFilaPostgreSql(string connectionString, ILogger<OuvinteDaFilaPostgreSql>? logger = null)
    : IOuvinteDaFila
{
    public const string Canal = "dante_fila";
    private static readonly TimeSpan EsperaParaReconectar = TimeSpan.FromSeconds(5);
    // Sem notificação por este tempo, a conexão é conferida (keepalive) antes de voltar a esperar.
    private static readonly TimeSpan Verificacao = TimeSpan.FromSeconds(30);

    public async Task OuvirAsync(Action aoNotificar, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(aoNotificar);
        var conexao = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var ouvinte = new NpgsqlConnection(conexao);
                await ouvinte.OpenAsync(cancellationToken);
                ouvinte.Notification += (_, _) => aoNotificar();
                await using (var listen = new NpgsqlCommand($"LISTEN {Canal}", ouvinte))
                    await listen.ExecuteNonQueryAsync(cancellationToken);
                // Notificações perdidas enquanto a conexão estava fora: o processador confere a fila agora.
                aoNotificar();
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (!await ouvinte.WaitAsync(Verificacao, cancellationToken))
                    {
                        await using var ping = new NpgsqlCommand("SELECT 1", ouvinte);
                        await ping.ExecuteScalarAsync(cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Exceptions do provider podem conter dados de conexão: só o tipo vai para o log.
                logger?.LogWarning("LISTEN da fila indisponível ({ErrorType}); o polling continua e a conexão é refeita.",
                    exception.GetType().Name);
                try
                {
                    await Task.Delay(EsperaParaReconectar, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
