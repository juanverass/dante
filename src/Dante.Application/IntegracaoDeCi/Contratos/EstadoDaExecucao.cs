namespace Dante.Application.IntegracaoDeCi;

// Estado observado de uma tentativa, de um job ou de uma etapa (AD-76). A ordem é a do ciclo de vida: as transições
// permitidas só avançam (TransicoesDaExecucaoDeCi). Os cinco últimos são finais e imutáveis dentro da tentativa.
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
