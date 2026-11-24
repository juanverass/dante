namespace Dante.Application.IntegracaoDeCi;

// Estado observado de uma tentativa, de um job ou de uma etapa (AD-76). Quais estados são finais e como avançam é
// definido estado a estado em TransicoesDaExecucaoDeCi, nunca pela ordem numérica. Valor fora desta lista é recusado
// na fronteira dos contratos: um estado desconhecido nunca vira resultado.
public enum EstadoDaExecucao
{
    NaFila,
    AguardandoRunner,
    EmAndamento,
    Sucesso,
    Falha,
    Cancelada,
    Expirada,
    Ignorada
}
