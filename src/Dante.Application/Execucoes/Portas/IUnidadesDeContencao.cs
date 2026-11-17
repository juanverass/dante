namespace Dante.Application.Execucoes;

// Unidades de contenção de agentes do usuário (scopes dante-agent-*, AD-63). Só a recuperação na inicialização usa
// (AD-67): lista para comparar com o ledger e para apenas as registradas por um boot anterior. Parar exige um nome
// no formato exato das unidades criadas pelo D.A.N.T.E.; qualquer outro é recusado sem chamar o gerenciador.
public interface IUnidadesDeContencao
{
    Task<IReadOnlyList<string>> ListarAsync(CancellationToken cancellationToken = default);

    Task PararAsync(string unidade, CancellationToken cancellationToken = default);
}
