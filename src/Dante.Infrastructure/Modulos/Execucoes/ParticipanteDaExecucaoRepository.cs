using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Infrastructure.Data.Orquestracao;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dante.Infrastructure.Modulos.Execucoes;

public sealed class ParticipanteDaExecucaoRepository(OrquestracaoDbContext context)
    : Repository<ParticipanteDaExecucao>(context), IParticipanteDaExecucaoRepository
{
    public async Task<IReadOnlyList<ParticipanteDaExecucao>> ListarDaExecucaoAsync(Guid idExecucao,
        CancellationToken cancellationToken = default) =>
        await DbSet.Where(x => x.IdExecucao == idExecucao).OrderBy(x => x.Papel).ToListAsync(cancellationToken);
}
