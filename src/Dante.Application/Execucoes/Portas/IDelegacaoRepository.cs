using Dante.Application.Comum;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Delegações rastreadas (AD-70, #309). A identidade de uma unidade de trabalho é (raiz, chave de deduplicação) entre as
// não terminais, garantida por índice único parcial; as contagens alimentam fan-out e orçamento da raiz.
public interface IDelegacaoRepository : IRepository<Delegacao>
{
    Task<Delegacao?> ObterNaoTerminalPorChaveAsync(Guid idExecucaoRaiz, string chaveDeDeduplicacao,
        CancellationToken cancellationToken = default);

    Task<Delegacao?> ObterDaSubordinadaAsync(Guid idExecucaoSubordinada, CancellationToken cancellationToken = default);

    // Delegações criadas pela execução de origem (fan-out) e pela árvore inteira (orçamento); recusadas não contam.
    Task<int> ContarDaOrigemAsync(Guid idExecucaoOrigem, CancellationToken cancellationToken = default);

    Task<int> ContarDaRaizAsync(Guid idExecucaoRaiz, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Delegacao>> ListarDaRaizAsync(Guid idExecucaoRaiz, CancellationToken cancellationToken = default);
}
