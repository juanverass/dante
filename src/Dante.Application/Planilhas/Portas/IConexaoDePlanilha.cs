namespace Dante.Application.Planilhas;

// Conta do provedor autorizada pelo usuário. Tokens, segredos e o fluxo de autorização são do adapter: a Application só
// inicia, consulta e revoga a conexão.
public interface IConexaoDePlanilha
{
    Task<EstadoDaConexaoDto> ObterEstadoAsync(CancellationToken cancellationToken = default);

    // Inicia uma autorização nova, substituindo uma pendente.
    Task<AutorizacaoDeConexao> IniciarAsync(CancellationToken cancellationToken = default);

    // Revoga no provedor quando possível e apaga a credencial local; false quando não havia conexão.
    Task<bool> DesconectarAsync(CancellationToken cancellationToken = default);
}
