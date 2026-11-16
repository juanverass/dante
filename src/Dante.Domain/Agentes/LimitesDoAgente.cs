namespace Dante.Domain.Agentes;

// Limites declarados de uma versão (AD-68); nulo é "sem limite próprio". ExecucoesSimultaneas vale desde já para as
// sessões do agente; os demais são lidos pelas entregas que executam etapas (fila, acompanhamento).
public sealed record LimitesDoAgente(
    int? ExecucoesSimultaneas = null,
    long? TokensPorExecucao = null,
    int? TurnosPorEtapa = null,
    int? MinutosPorEtapa = null)
{
    public const int MaximoDeExecucoesSimultaneas = 10;
    public const int MaximoDeTurnosPorEtapa = 200;
    public const int MaximoDeMinutosPorEtapa = 24 * 60;

    public static LimitesDoAgente Nenhum { get; } = new();

    internal void Validar()
    {
        if (ExecucoesSimultaneas is < 1 or > MaximoDeExecucoesSimultaneas)
            throw new ArgumentOutOfRangeException(nameof(ExecucoesSimultaneas),
                $"Execuções simultâneas devem ficar entre 1 e {MaximoDeExecucoesSimultaneas}.");
        if (TokensPorExecucao is < 1)
            throw new ArgumentOutOfRangeException(nameof(TokensPorExecucao), "Tokens por execução devem ser positivos.");
        if (TurnosPorEtapa is < 1 or > MaximoDeTurnosPorEtapa)
            throw new ArgumentOutOfRangeException(nameof(TurnosPorEtapa),
                $"Turnos por etapa devem ficar entre 1 e {MaximoDeTurnosPorEtapa}.");
        if (MinutosPorEtapa is < 1 or > MaximoDeMinutosPorEtapa)
            throw new ArgumentOutOfRangeException(nameof(MinutosPorEtapa),
                $"Minutos por etapa devem ficar entre 1 e {MaximoDeMinutosPorEtapa}.");
    }
}
