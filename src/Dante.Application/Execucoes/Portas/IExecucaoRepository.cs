using Dante.Application.Comum;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

public interface IExecucaoRepository : IRepository<Execucao>
{
    // Próximo número da sequência persistente, sempre acima de acimaDe; nunca se repete, nem depois de reiniciar.
    Task<long> ReservarNumeroAsync(long acimaDe = 0, CancellationToken cancellationToken = default);
    Task<Execucao?> ObterPorNumeroAsync(long numero, CancellationToken cancellationToken = default);

    // Leitura sem rastreamento (#305): não entra na unidade de trabalho nem fixa valores antigos no contexto.
    Task<Execucao?> ConsultarPorNumeroAsync(long numero, CancellationToken cancellationToken = default);

    // Carrega rastreada e trava a linha (FOR UPDATE) até o fim da transação corrente, que precisa existir. Quem também
    // trava o trabalho da execução trava o trabalho primeiro, na mesma ordem das gravações sob posse.
    Task<Execucao?> ObterParaAtualizarAsync(Guid id, CancellationToken cancellationToken = default);

    // Do proprietário, sem rastreamento, da mais recente para a mais antiga: todas as não terminais e até
    // limiteDeEncerradas das terminais.
    Task<IReadOnlyList<Execucao>> ListarDoProprietarioAsync(string canal, long idDoUsuarioNoCanal, int limiteDeEncerradas,
        CancellationToken cancellationToken = default);
}
