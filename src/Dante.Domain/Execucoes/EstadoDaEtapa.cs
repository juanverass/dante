namespace Dante.Domain.Execucoes;

// Concluida, Falhou, Cancelada e Ignorada são terminais; Interrompida volta a Pronta numa nova tentativa.
public enum EstadoDaEtapa
{
    Pendente,
    Pronta,
    EmExecucao,
    AguardandoAprovacao,
    AguardandoEntrada,
    Interrompida,
    Concluida,
    Falhou,
    Cancelada,
    Ignorada
}
