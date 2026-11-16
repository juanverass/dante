namespace Dante.Domain.Execucoes;

// Concluida, Falhou e Cancelada são terminais; Interrompida e Bloqueada podem ser retomadas (#297, AD-66).
public enum EstadoDaExecucao
{
    Pendente,
    EmExecucao,
    AguardandoAprovacao,
    AguardandoEntrada,
    AguardandoDelegacoes,
    Bloqueada,
    Interrompida,
    Concluida,
    Falhou,
    Cancelada
}
