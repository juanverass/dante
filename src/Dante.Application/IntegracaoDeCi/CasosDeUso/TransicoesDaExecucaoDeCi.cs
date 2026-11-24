namespace Dante.Application.IntegracaoDeCi;

// Matriz de transições de uma tentativa, de um job ou de uma etapa (AD-76). O polling amostra o provedor, então um
// salto (NaFila → Sucesso) é válido; voltar nunca é: uma observação que regride vem de cache ou de réplica atrasada e é
// ignorada. Estado final é imutável dentro da tentativa; reexecução é tentativa nova, com identidade nova.
//
//   NaFila ──→ AguardandoRunner ──→ EmAndamento ──→ Sucesso | Falha | Cancelada | Expirada
//     │               │                               ↑
//     └───────────────┴──→ Ignorada                   └── também direto da fila ou da espera por runner
//
// A classificação é explícita por estado, nunca pela ordem numérica do enum (revisão do PR #327): um valor que não é
// estado conhecido não é final nem avança nada; é recusado.
public static class TransicoesDaExecucaoDeCi
{
    public static bool Final(EstadoDaExecucao estado) => Etapa(estado) == 3;

    public static bool Permitida(EstadoDaExecucao de, EstadoDaExecucao para)
    {
        var origem = Etapa(de);
        var destino = Etapa(para);
        if (origem == 3) return false;
        if (para == EstadoDaExecucao.Ignorada) return de != EstadoDaExecucao.EmAndamento;
        return destino > origem;
    }

    // O estado que vale depois de uma observação: a observada, se avança; a atual, se repete ou regride.
    public static EstadoDaExecucao Avancar(EstadoDaExecucao atual, EstadoDaExecucao observado) =>
        Permitida(atual, observado) ? observado : atual;

    private static int Etapa(EstadoDaExecucao estado) => estado switch
    {
        EstadoDaExecucao.NaFila => 0,
        EstadoDaExecucao.AguardandoRunner => 1,
        EstadoDaExecucao.EmAndamento => 2,
        EstadoDaExecucao.Sucesso or EstadoDaExecucao.Falha or EstadoDaExecucao.Cancelada or EstadoDaExecucao.Expirada
            or EstadoDaExecucao.Ignorada => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(estado), estado, "Estado de execução de CI desconhecido.")
    };
}
