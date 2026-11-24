namespace Dante.Application.IntegracaoDeCi;

// Por que uma execução não terminou com sucesso, só quando há evidência (MatrizDeFalhasDeCi). Sem evidência suficiente
// a categoria é Indeterminada: vermelho no CI nunca é, por si só, erro de teste, falta de runner ou fim de cota.
public enum CategoriaDaFalhaDeCi
{
    ErroNoCodigoOuTeste,
    RunnerIndisponivel,
    LimiteDeMinutosOuCobranca,
    LogsInacessiveis,
    Indeterminada
}
