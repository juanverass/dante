using Dante.Application.Comum;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Delegações rastreadas (AD-70, #309). A identidade de uma unidade de trabalho é (raiz, chave de deduplicação), em
// qualquer estado, garantida por índice único; as contagens alimentam fan-out e orçamento da raiz.
public interface IDelegacaoRepository : IRepository<Delegacao>
{
    // A delegação daquela unidade, qualquer que seja o estado (inclusive terminal): quem repete o pedido recebe a
    // original, com o estado e o resultado gravados, pela regra de Delegacao.ConferirRepeticao.
    Task<Delegacao?> ObterPorChaveAsync(Guid idExecucaoRaiz, string chaveDeDeduplicacao,
        CancellationToken cancellationToken = default);

    Task<Delegacao?> ObterDaSubordinadaAsync(Guid idExecucaoSubordinada, CancellationToken cancellationToken = default);

    // Delegações criadas pela execução de origem (fan-out) e pela árvore inteira (orçamento); recusadas não contam.
    Task<int> ContarDaOrigemAsync(Guid idExecucaoOrigem, CancellationToken cancellationToken = default);

    Task<int> ContarDaRaizAsync(Guid idExecucaoRaiz, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Delegacao>> ListarDaRaizAsync(Guid idExecucaoRaiz, CancellationToken cancellationToken = default);
}
