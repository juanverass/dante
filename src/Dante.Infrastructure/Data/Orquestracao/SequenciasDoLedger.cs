using Microsoft.EntityFrameworkCore;

namespace Dante.Infrastructure.Data.Orquestracao;

// Reserva de números nas sequências do ledger (AD-66).
internal static class SequenciasDoLedger
{
    // acimaDe: maior número que o processo já usou localmente (contador local enquanto o banco estava fora). Uma
    // sequência atrás dele é avançada até ele antes de reservar, para o espelho voltar a valer sem repetir código.
    // nextval e setval não são atômicos juntos: toda reserva desta sequência passa por um advisory lock de
    // transação, então nenhuma outra conexão reserva entre a leitura e o avanço, e a sequência nunca recua.
    internal static async Task<long> ReservarAsync(DbContext contexto, string sequencia, long acimaDe,
        CancellationToken cancellationToken)
    {
        var nome = $"{OrquestracaoDbContext.Schema}.{sequencia}";
        var propria = contexto.Database.CurrentTransaction is null
            ? await contexto.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            await contexto.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext({0}))", [nome],
                cancellationToken);
            var numero = await ProximoAsync(contexto, sequencia, cancellationToken);
            if (numero <= acimaDe)
            {
                await contexto.Database.SqlQueryRaw<long>($"SELECT setval('{nome}', {{0}}) AS \"Value\"", acimaDe)
                    .SingleAsync(cancellationToken);
                numero = await ProximoAsync(contexto, sequencia, cancellationToken);
            }
            if (propria is not null) await propria.CommitAsync(cancellationToken);
            return numero;
        }
        finally
        {
            if (propria is not null) await propria.DisposeAsync();
        }
    }

    // Sem reconciliação (eventos): nextval já é atômico.
    internal static Task<long> ProximoAsync(DbContext contexto, string sequencia, CancellationToken cancellationToken) =>
        contexto.Database.SqlQueryRaw<long>($"SELECT nextval('{OrquestracaoDbContext.Schema}.{sequencia}') AS \"Value\"")
            .SingleAsync(cancellationToken);
}
