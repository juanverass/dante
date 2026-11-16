using Microsoft.EntityFrameworkCore;

namespace Dante.Infrastructure.Data.Orquestracao;

// Reserva de números nas sequências do ledger (AD-66).
internal static class SequenciasDoLedger
{
    // acimaDe: maior número que o processo já usou localmente (contador local enquanto o banco estava fora). Uma
    // sequência atrás dele é avançada até ele antes de reservar, para o espelho voltar a valer sem repetir código.
    internal static async Task<long> ReservarAsync(DbContext contexto, string sequencia, long acimaDe,
        CancellationToken cancellationToken)
    {
        var nome = $"{OrquestracaoDbContext.Schema}.{sequencia}";
        var numero = await Proximo(contexto, nome, cancellationToken);
        if (numero > acimaDe) return numero;
        // GREATEST com last_value: nunca recua uma sequência que outro escritor já avançou além de acimaDe.
        await contexto.Database.SqlQueryRaw<long>(
            $"SELECT setval('{nome}', GREATEST({{0}}, last_value)) AS \"Value\" FROM {nome}", acimaDe)
            .SingleAsync(cancellationToken);
        return await Proximo(contexto, nome, cancellationToken);
    }

    private static Task<long> Proximo(DbContext contexto, string nome, CancellationToken cancellationToken) =>
        contexto.Database.SqlQueryRaw<long>($"SELECT nextval('{nome}') AS \"Value\"").SingleAsync(cancellationToken);
}
