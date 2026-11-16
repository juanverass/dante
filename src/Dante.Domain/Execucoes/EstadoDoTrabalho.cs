namespace Dante.Domain.Execucoes;

// Pendente: aguardando disponivel_em. Reivindicado: com dono e lease. Concluido, Falhou e Descartado são terminais.
public enum EstadoDoTrabalho
{
    Pendente,
    Reivindicado,
    Concluido,
    Falhou,
    Descartado
}
