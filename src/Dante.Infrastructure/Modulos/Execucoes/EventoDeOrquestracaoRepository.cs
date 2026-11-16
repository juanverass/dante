using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Infrastructure.Data.Orquestracao;
using Dante.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dante.Infrastructure.Modulos.Execucoes;

public sealed class EventoDeOrquestracaoRepository(OrquestracaoDbContext context)
    : Repository<EventoDeOrquestracao>(context), IEventoDeOrquestracaoRepository
{
    // A sequência é reservada na ordem em que os eventos são adicionados: o EF ordena os INSERTs de uma mesma tabela
    // pela chave, e o default da coluna daria a eventos da mesma transação uma ordem diferente da lógica.
    public override async Task AdicionarAsync(EventoDeOrquestracao entidade, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entidade);
        var sequencia = await SequenciasDoLedger.ReservarAsync(Context, OrquestracaoDbContext.SequenciaDeEventos, 0,
            cancellationToken);
        await base.AdicionarAsync(entidade, cancellationToken);
        Context.Entry(entidade).Property(evento => evento.Sequencia).CurrentValue = sequencia;
    }

    public async Task<IReadOnlyList<EventoDeOrquestracao>> ListarDaExecucaoAsync(Guid idExecucao,
        CancellationToken cancellationToken = default) =>
        await DbSet.Where(x => x.IdExecucao == idExecucao).OrderBy(x => x.Sequencia).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<EventoDeOrquestracao>> ListarDaSessaoAsync(Guid idSessaoDeAgente,
        CancellationToken cancellationToken = default) =>
        await DbSet.Where(x => x.IdSessaoDeAgente == idSessaoDeAgente).OrderBy(x => x.Sequencia)
            .ToListAsync(cancellationToken);
}
