using System.Diagnostics.Metrics;

namespace Dante.Worker.Execucoes;

// Contadores da fila durável (AD-67), sem conteúdo: dimensões só por tipo de execução e motivo curto. Observáveis por
// dotnet-counters/OpenTelemetry no medidor "Dante.Orquestracao.Fila"; os mesmos fatos saem no log estruturado.
public sealed class MetricasDaFila : IDisposable
{
    public const string NomeDoMedidor = "Dante.Orquestracao.Fila";
    private readonly Meter medidor = new(NomeDoMedidor);

    public MetricasDaFila()
    {
        Reivindicados = medidor.CreateCounter<long>("dante.fila.reivindicados");
        Concluidos = medidor.CreateCounter<long>("dante.fila.concluidos");
        LeasesPerdidos = medidor.CreateCounter<long>("dante.fila.leases_perdidos");
        Retentativas = medidor.CreateCounter<long>("dante.fila.retentativas");
        Deduplicados = medidor.CreateCounter<long>("dante.fila.deduplicados");
        Interrompidos = medidor.CreateCounter<long>("dante.fila.interrompidos");
        Descartados = medidor.CreateCounter<long>("dante.fila.descartados");
        Esgotados = medidor.CreateCounter<long>("dante.fila.esgotados");
        Cancelados = medidor.CreateCounter<long>("dante.fila.cancelados");
        SemCapacidade = medidor.CreateCounter<long>("dante.fila.sem_capacidade_de_worktree");
        UnidadesParadas = medidor.CreateCounter<long>("dante.fila.recuperacao.unidades_paradas");
        UnidadesDesconhecidas = medidor.CreateCounter<long>("dante.fila.recuperacao.unidades_desconhecidas");
    }

    public Counter<long> Reivindicados { get; }
    public Counter<long> Concluidos { get; }
    public Counter<long> LeasesPerdidos { get; }
    public Counter<long> Retentativas { get; }
    public Counter<long> Deduplicados { get; }
    public Counter<long> Interrompidos { get; }
    public Counter<long> Descartados { get; }
    public Counter<long> Esgotados { get; }
    public Counter<long> Cancelados { get; }
    public Counter<long> SemCapacidade { get; }
    public Counter<long> UnidadesParadas { get; }
    public Counter<long> UnidadesDesconhecidas { get; }

    public void Dispose() => medidor.Dispose();
}
