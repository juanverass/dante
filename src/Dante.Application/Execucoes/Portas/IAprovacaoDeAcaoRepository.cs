using Dante.Application.Comum;
using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Aprovações de ação protegida do gateway (AD-72, #311). Persistência só: o gateway e o adaptador de decisão vêm na
// entrega seguinte. A execução única é a gravação da transição para EmExecucao sob o xmin da linha (CAS).
public interface IAprovacaoDeAcaoRepository : IRepository<AprovacaoDeAcao>
{
    Task<IReadOnlyList<AprovacaoDeAcao>> ListarDaExecucaoAsync(Guid idExecucao, CancellationToken cancellationToken = default);

    // Pedidos pendentes ou aprovados e ainda não executados do dono, mais antigos primeiro.
    Task<IReadOnlyList<AprovacaoDeAcao>> ListarEmAbertoDoDonoAsync(string canal, long idDoDono,
        CancellationToken cancellationToken = default);

    // Em aberto com o prazo vencido em `agora` (para expirar).
    Task<IReadOnlyList<AprovacaoDeAcao>> ListarVencidasAsync(DateTimeOffset agora, int limite,
        CancellationToken cancellationToken = default);
}
