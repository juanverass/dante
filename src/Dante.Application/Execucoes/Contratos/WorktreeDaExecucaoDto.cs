using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Reserva de worktree de uma execução (AD-71), como o Worker precisa para criar, validar e entregar ao agente.
public sealed record WorktreeDaExecucaoDto(Guid Id, Guid IdExecucao, string Alias, bool Principal, string Caminho,
    string CaminhoDoRepositorio, string Branch, string ShaBase, EstadoDaReservaDeWorktree Estado, string? Motivo)
{
    public WorktreePlanejada Planejada => new(Alias, CaminhoDoRepositorio, Caminho, Branch, ShaBase);

    internal static WorktreeDaExecucaoDto De(ReservaDeWorktree reserva) => new(reserva.Id, reserva.IdExecucao,
        reserva.Alias, reserva.Principal, reserva.Caminho, reserva.CaminhoDoRepositorio, reserva.Branch, reserva.ShaBase,
        reserva.Estado, reserva.Motivo);
}
