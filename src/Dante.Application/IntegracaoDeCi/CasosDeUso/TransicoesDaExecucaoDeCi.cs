namespace Dante.Application.IntegracaoDeCi;

// Matriz de transições de uma tentativa, de um job ou de uma etapa (AD-76). O polling amostra o provedor, então um
// salto (NaFila → Sucesso) é válido; voltar nunca é: uma observação que regride vem de cache ou de réplica atrasada e é
// ignorada. Estado final é imutável dentro da tentativa; reexecução é tentativa nova, com identidade nova.
//
//   NaFila ──→ AguardandoRunner ──→ EmAndamento ──→ Sucesso | Falha | Cancelada | Expirada
//     │               │                               ↑
//     └───────────────┴──→ Ignorada                   └── também direto da fila ou da espera por runner
public static class TransicoesDaExecucaoDeCi
{
    public static bool Final(EstadoDaExecucao estado) => estado >= EstadoDaExecucao.Sucesso;

    public static bool Permitida(EstadoDaExecucao de, EstadoDaExecucao para) => (de, para) switch
    {
        _ when Final(de) => false,
        (_, EstadoDaExecucao.Ignorada) => de != EstadoDaExecucao.EmAndamento,
        _ => para > de
    };

    // O estado que vale depois de uma observação: a observada, se avança; a atual, se repete ou regride.
    public static EstadoDaExecucao Avancar(EstadoDaExecucao atual, EstadoDaExecucao observado) =>
        Permitida(atual, observado) ? observado : atual;
}
