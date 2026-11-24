namespace Dante.Application.IntegracaoDeCi;

// Estado agregado do CI para um repositório e um head SHA (o "check" que o PR mostra), separado do estado de cada job e
// de cada tentativa. Os três últimos dizem que o D.A.N.T.E. não sabe o resultado, nunca que o CI falhou.
public enum EstadoDoCheckDeCi
{
    SemWorkflowConfigurado,
    SemExecucao,
    NaFila,
    AguardandoRunner,
    EmAndamento,
    Sucesso,
    Falha,
    Cancelado,
    Expirado,
    Indisponivel,
    SemPermissao
}
